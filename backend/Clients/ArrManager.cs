using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Clients;

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

            // Try Radarr instances first (for movies)
            foreach (var radarrClient in _radarrClients)
            {
                try
                {
                    var success = await radarrClient.DeleteFileAsync(filePath, ct);
                    if (success)
                    {
                        Log.Information("Successfully deleted file '{FilePath}' via Radarr instance", filePath);
                        anySuccess = true;
                        // Don't break here - the file might exist in multiple instances
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete file '{FilePath}' via Radarr instance", filePath);
                }
            }

            // Try Sonarr instances (for TV shows)
            foreach (var sonarrClient in _sonarrClients)
            {
                try
                {
                    var success = await sonarrClient.DeleteFileAsync(filePath, ct);
                    if (success)
                    {
                        Log.Information("Successfully deleted file '{FilePath}' via Sonarr instance", filePath);
                        anySuccess = true;
                        // Don't break here - the file might exist in multiple instances
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete file '{FilePath}' via Sonarr instance", filePath);
                }
            }

            if (!anySuccess)
            {
                Log.Warning("File '{FilePath}' was not found in any configured Radarr or Sonarr instances", filePath);
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
