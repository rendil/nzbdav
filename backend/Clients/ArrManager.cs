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
    private readonly List<RadarrClient> _radarrClients = new();
    private readonly List<SonarrClient> _sonarrClients = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public ArrManager(ConfigManager configManager)
    {
        _configManager = configManager;
        InitializeClients();
    }

    private void InitializeClients()
    {
        // Initialize Radarr clients
        var radarrInstances = GetRadarrInstances();
        foreach (var instance in radarrInstances)
        {
            var client = new RadarrClient(instance.BaseUrl, instance.ApiKey, instance.Name);
            _radarrClients.Add(client);
            Log.Information("Initialized Radarr client for instance '{InstanceName}' at {BaseUrl}", 
                instance.Name, instance.BaseUrl);
        }

        // Initialize Sonarr clients
        var sonarrInstances = GetSonarrInstances();
        foreach (var instance in sonarrInstances)
        {
            var client = new SonarrClient(instance.BaseUrl, instance.ApiKey, instance.Name);
            _sonarrClients.Add(client);
            Log.Information("Initialized Sonarr client for instance '{InstanceName}' at {BaseUrl}", 
                instance.Name, instance.BaseUrl);
        }
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
                        var (success, movieId) = await radarrClient.DeleteFileWithMovieIdAsync(filePath, ct);
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
                                await radarrClient.SearchForMovieAsync(movieId.Value, ct);
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
                foreach (var sonarrClient in _sonarrClients)
                {
                    try
                    {
                        var (success, episodeIds) = await sonarrClient.DeleteFileWithEpisodeIdsAsync(filePath, ct);
                        if (success)
                        {
                            Log.Information("Successfully deleted file '{FilePath}' via Sonarr instance '{InstanceName}'", 
                                filePath, sonarrClient.InstanceName);
                            anySuccess = true;
                            
                            // Trigger search for replacement if we have episode IDs
                            if (episodeIds?.Length > 0)
                            {
                                Log.Information("Triggering search for replacement episodes (IDs: {EpisodeIds}) in Sonarr instance '{InstanceName}'", 
                                    string.Join(", ", episodeIds), sonarrClient.InstanceName);
                                await sonarrClient.SearchForEpisodesAsync(episodeIds, ct);
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

    public async Task<bool> UnmonitorFileFromArrAsync(string filePath, CancellationToken ct = default)
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
                    try
                    {
                        var success = await radarrClient.UnmonitorMovieAsync(filePath, ct);
                        if (success)
                        {
                            Log.Information("Successfully unmonitored movie for file '{FilePath}' via Radarr instance '{InstanceName}'", 
                                filePath, radarrClient.InstanceName);
                            anySuccess = true;
                            // Don't break here - the file might exist in multiple instances
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to unmonitor movie for file '{FilePath}' via Radarr instance '{InstanceName}'", 
                            filePath, radarrClient.InstanceName);
                    }
                }
            }

            if (contentType == ContentType.TvShow || contentType == ContentType.Unknown)
            {
                // Try Sonarr instances (for TV shows and unknown content)
                foreach (var sonarrClient in _sonarrClients)
                {
                    try
                    {
                        var success = await sonarrClient.UnmonitorSeriesAsync(filePath, ct);
                        if (success)
                        {
                            Log.Information("Successfully unmonitored series for file '{FilePath}' via Sonarr instance '{InstanceName}'", 
                                filePath, sonarrClient.InstanceName);
                            anySuccess = true;
                            // Don't break here - the file might exist in multiple instances
                        }
                    }
                    catch (Exception ex)
                    {
                        var logLevel = contentType == ContentType.TvShow ? "Warning" : "Debug";
                        if (logLevel == "Warning")
                        {
                            Log.Warning(ex, "Failed to unmonitor series for file '{FilePath}' via Sonarr instance '{InstanceName}'", 
                                filePath, sonarrClient.InstanceName);
                        }
                        else
                        {
                            Log.Debug(ex, "Failed to unmonitor series for file '{FilePath}' via Sonarr instance '{InstanceName}' - likely a movie file", 
                                filePath, sonarrClient.InstanceName);
                        }
                    }
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

        // Test Radarr connections
        foreach (var radarrClient in _radarrClients)
        {
            var success = await radarrClient.TestConnectionAsync(ct);
            if (!success)
            {
                allSuccessful = false;
            }
        }

        // Test Sonarr connections
        foreach (var sonarrClient in _sonarrClients)
        {
            var success = await sonarrClient.TestConnectionAsync(ct);
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

    private List<ArrInstance> GetRadarrInstances()
    {
        var instances = new List<ArrInstance>();
        var instanceCount = GetInstanceCount("radarr");

        for (int i = 0; i < instanceCount; i++)
        {
            var name = _configManager.GetConfigValue($"radarr.{i}.name") ?? $"Radarr-{i}";
            var baseUrl = _configManager.GetConfigValue($"radarr.{i}.url");
            var apiKey = _configManager.GetConfigValue($"radarr.{i}.api_key");

            if (!string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(apiKey))
            {
                instances.Add(new ArrInstance
                {
                    Name = name,
                    BaseUrl = baseUrl.TrimEnd('/'),
                    ApiKey = apiKey
                });
            }
        }

        return instances;
    }

    private List<ArrInstance> GetSonarrInstances()
    {
        var instances = new List<ArrInstance>();
        var instanceCount = GetInstanceCount("sonarr");

        for (int i = 0; i < instanceCount; i++)
        {
            var name = _configManager.GetConfigValue($"sonarr.{i}.name") ?? $"Sonarr-{i}";
            var baseUrl = _configManager.GetConfigValue($"sonarr.{i}.url");
            var apiKey = _configManager.GetConfigValue($"sonarr.{i}.api_key");

            if (!string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(apiKey))
            {
                instances.Add(new ArrInstance
                {
                    Name = name,
                    BaseUrl = baseUrl.TrimEnd('/'),
                    ApiKey = apiKey
                });
            }
        }

        return instances;
    }

    private int GetInstanceCount(string appType)
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
            @"/tv/",
            @"/shows/",
            @"/series/",
            @"/television/",
            @"s\d{1,2}e\d{1,2}",     // S01E01 pattern
            @"season\s*\d+",          // Season 1 pattern
            @"\d{1,2}x\d{1,2}",      // 1x01 pattern
            @"episode\s*\d+",         // Episode pattern
        };
        
        // Common movie patterns
        var moviePatterns = new[]
        {
            @"/movies/",
            @"/films/",
            @"/cinema/",
            @"\(\d{4}\)",             // (2023) year pattern
            @"\.\d{4}\.",             // .2023. year pattern
            @"bluray",
            @"brrip",
            @"webrip",
            @"dvdrip"
        };
        
        // Check for TV show patterns
        foreach (var pattern in tvPatterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(normalizedPath, pattern))
            {
                return ContentType.TvShow;
            }
        }
        
        // Check for movie patterns
        foreach (var pattern in moviePatterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(normalizedPath, pattern))
            {
                return ContentType.Movie;
            }
        }
        
        // Default to unknown if we can't determine
        return ContentType.Unknown;
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

public class ArrInstance
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}
