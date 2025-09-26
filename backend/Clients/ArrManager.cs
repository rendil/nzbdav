using System.Linq;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Clients;

public enum ContentType
{
    Unknown,
    Movie,
    TvShow
}

public class ArrManager : IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly List<ArrClient> _radarrClients = new();
    private readonly List<ArrClient> _sonarrClients = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public ArrManager(ConfigManager configManager)
    {
        _configManager = configManager;
        InitializeClients();
    }

    private void InitializeClients()
    {
        _radarrClients.AddRange(GetClients(ArrAppType.Radarr));
        _sonarrClients.AddRange(GetClients(ArrAppType.Sonarr));
    }

    public async Task<bool> DeleteFileFromArrAsync(string filePath, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var anySuccess = false;
            var contentType = DetectContentType(filePath);

            Log.Debug("Detected content type '{ContentType}' for file: {FilePath}", contentType, filePath);

            if (contentType == ContentType.Movie || contentType == ContentType.Unknown)
            {
                // Try Radarr instances (for movies and unknown content)
                foreach (var radarrClient in _radarrClients)
                {
                    try
                    {
                        var (success, movieId) = await radarrClient.DeleteFileAsync(filePath, ct);
                        if (success)
                        {
                            Log.Information("Successfully deleted file '{FilePath}' via Radarr instance '{InstanceName}'",
                                filePath, radarrClient.InstanceName);
                            anySuccess = true;

                            // Trigger search for replacement if we have a movie ID
                            if (movieId.HasValue)
                            {
                                Log.Information("Triggering search for replacement movie (ID: {MovieId}) in Radarr instance '{InstanceName}'",
                                    movieId.Value, radarrClient.InstanceName);
                                await radarrClient.TriggerSearchByIdAsync(movieId.Value, ct);
                            }
                            // Don't break here - the file might exist in multiple instances
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to delete file '{FilePath}' via Radarr instance '{InstanceName}'",
                            filePath, radarrClient.InstanceName);
                    }
                }
            }

            if (contentType == ContentType.TvShow || contentType == ContentType.Unknown)
            {
                // Try Sonarr instances (for TV shows and unknown content)
                Log.Debug("Attempting to delete from {SonarrCount} configured Sonarr instances", _sonarrClients.Count);
                foreach (var sonarrClient in _sonarrClients)
                {
                    try
                    {
                        Log.Debug("Trying Sonarr instance '{InstanceName}' for file deletion", sonarrClient.InstanceName);
                        var (success, episodeId) = await sonarrClient.DeleteFileAsync(filePath, ct);
                        if (success)
                        {
                            Log.Information("Successfully deleted file '{FilePath}' via Sonarr instance '{InstanceName}'",
                                filePath, sonarrClient.InstanceName);
                            anySuccess = true;

                            // Trigger search for replacement if we have episode IDs
                            if (episodeId.HasValue)
                            {
                                Log.Information("Triggering search for replacement episodes (IDs: {EpisodeId}) in Sonarr instance '{InstanceName}'",
                                    episodeId, sonarrClient.InstanceName);
                                await sonarrClient.TriggerSearchByIdAsync(episodeId.Value, ct);
                            }
                            // Don't break here - the file might exist in multiple instances
                        }
                    }
                    catch (Exception ex)
                    {
                        // Only log at Warning level if we specifically detected this as a TV show
                        var logLevel = contentType == ContentType.TvShow ? "Warning" : "Debug";
                        if (logLevel == "Warning")
                        {
                            Log.Warning(ex, "Failed to delete file '{FilePath}' via Sonarr instance '{InstanceName}'",
                                filePath, sonarrClient.InstanceName);
                        }
                        else
                        {
                            Log.Debug(ex, "Failed to delete file '{FilePath}' via Sonarr instance '{InstanceName}' - likely a movie file",
                                filePath, sonarrClient.InstanceName);
                        }
                    }
                }
            }

            if (!anySuccess)
            {
                Log.Warning("File '{FilePath}' was not found in any configured {ServiceTypes} instances",
                    filePath, GetServiceTypesString(contentType));
            }

            return anySuccess;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> MonitorFileInArrAsync(string filePath, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var anySuccess = false;
            var contentType = DetectContentType(filePath);

            Log.Debug("Attempting to monitor content type '{ContentType}' for file: {FilePath}", contentType, filePath);

            if (contentType == ContentType.Movie || contentType == ContentType.Unknown)
            {
                // Try Radarr instances (for movies and unknown content)
                foreach (var radarrClient in _radarrClients)
                {
                    await radarrClient.MonitorFileAsync(filePath, ct);
                }
            }

            if (contentType == ContentType.TvShow || contentType == ContentType.Unknown)
            {
                // Try Sonarr instances (for TV shows and unknown content)
                foreach (var sonarrClient in _sonarrClients)
                {
                    await sonarrClient.MonitorFileAsync(filePath, ct);
                }
            }

            if (!anySuccess)
            {
                Log.Debug("File '{FilePath}' was not found for monitoring in any configured {ServiceTypes} instances",
                    filePath, GetServiceTypesString(contentType));
            }

            return anySuccess;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> UnmonitorFileInArrAsync(string filePath, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var anySuccess = false;
            var contentType = DetectContentType(filePath);

            Log.Debug("Attempting to unmonitor content type '{ContentType}' for file: {FilePath}", contentType, filePath);

            if (contentType == ContentType.Movie || contentType == ContentType.Unknown)
            {
                // Try Radarr instances (for movies and unknown content)
                foreach (var radarrClient in _radarrClients)
                {
                    var success = await radarrClient.UnmonitorFileAsync(filePath, ct);
                    if (success) anySuccess = true;
                }
            }

            if (contentType == ContentType.TvShow || contentType == ContentType.Unknown)
            {
                // Try Sonarr instances (for TV shows and unknown content)
                foreach (var sonarrClient in _sonarrClients)
                {
                    var success = await sonarrClient.UnmonitorFileAsync(filePath, ct);
                    if (success) anySuccess = true;
                }
            }

            if (!anySuccess)
            {
                Log.Debug("File '{FilePath}' was not found for unmonitoring in any configured {ServiceTypes} instances",
                    filePath, GetServiceTypesString(contentType));
            }

            return anySuccess;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> TestAllConnectionsAsync(CancellationToken ct = default)
    {
        var allSuccessful = true;

        var allClients = _radarrClients.Cast<ArrClient>().Concat(_sonarrClients);

        // Combine the lists and test connections
        foreach (var client in allClients)
        {
            var success = await client.TestConnectionAsync(ct);
            if (!success)
            {
                allSuccessful = false;
            }
        }

        return allSuccessful;
    }

    public void RefreshClients()
    {
        // Dispose existing clients
        foreach (var client in _radarrClients)
        {
            client.Dispose();
        }
        foreach (var client in _sonarrClients)
        {
            client.Dispose();
        }

        _radarrClients.Clear();
        _sonarrClients.Clear();

        // Reinitialize with updated configuration
        InitializeClients();
    }


    private List<ArrClient> GetClients(ArrAppType appType)
    {
        var clients = new List<ArrClient>();
        var instanceCount = GetInstanceCount(appType);
        var lowerCaseAppType = appType.ToString().ToLowerInvariant();

        for (int i = 0; i < instanceCount; i++)
        {
            var name = _configManager.GetConfigValue($"{lowerCaseAppType}.{i}.name") ?? $"{appType}-{i}";
            var baseUrl = _configManager.GetConfigValue($"{lowerCaseAppType}.{i}.url");
            var apiKey = _configManager.GetConfigValue($"{lowerCaseAppType}.{i}.api_key");

            if (!string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(apiKey))
            {
                clients.Add(ArrClient.CreateClient(appType, baseUrl.TrimEnd('/'), apiKey, name));
            }
        }

        return clients;
    }

    private int GetInstanceCount(ArrAppType appType)
    {
        // Look for the highest numbered instance to determine count
        var maxIndex = -1;
        for (int i = 0; i < 10; i++) // Support up to 10 instances of each
        {
            var url = _configManager.GetConfigValue($"{appType}.{i}.url");
            if (!string.IsNullOrEmpty(url))
            {
                maxIndex = i;
            }
        }
        return maxIndex + 1;
    }

    public List<string> GetConfiguredInstances()
    {
        var instances = new List<string>();

        instances.AddRange(_radarrClients.Select(c => $"Radarr: {c.InstanceName}"));
        instances.AddRange(_sonarrClients.Select(c => $"Sonarr: {c.InstanceName}"));

        return instances;
    }

    private ContentType DetectContentType(string filePath)
    {
        // Normalize the path for analysis
        var normalizedPath = filePath.Replace('\\', '/').ToLowerInvariant();

        // Common TV show patterns
        var tvPatterns = new[]
        {
            @"s\d{1,2}e\d{1,3}",     // S01E01 pattern
            @"season\s*\d+",          // Season 1 pattern
            @"\d{1,2}x\d{1,3}",      // 1x01 pattern
        };

        // Check for TV show patterns
        foreach (var pattern in tvPatterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(normalizedPath, pattern))
            {
                return ContentType.TvShow;
            }
        }

        // Default to movie if we can't determine it's a tv show
        return ContentType.Movie;
    }

    private string GetServiceTypesString(ContentType contentType)
    {
        return contentType switch
        {
            ContentType.Movie => "Radarr",
            ContentType.TvShow => "Sonarr",
            ContentType.Unknown => "Radarr/Sonarr",
            _ => "Radarr/Sonarr"
        };
    }

    public void Dispose()
    {
        foreach (var client in _radarrClients)
        {
            client.Dispose();
        }
        foreach (var client in _sonarrClients)
        {
            client.Dispose();
        }
        _semaphore?.Dispose();
    }
}