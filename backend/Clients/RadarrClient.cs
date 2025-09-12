using System.Text.Json.Serialization;
using Serilog;

namespace NzbWebDAV.Clients;

public class RadarrClient : ArrClient
{
    public RadarrClient(string baseUrl, string apiKey, string instanceName) 
        : base(baseUrl, apiKey, instanceName)
    {
    }

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await GetAsync<RadarrSystemStatus>("/api/v3/system/status", ct);
            if (status != null)
            {
                Log.Information("Successfully connected to Radarr instance '{InstanceName}' (v{Version})", 
                    _instanceName, status.Version);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to test connection to Radarr instance '{InstanceName}'", _instanceName);
            return false;
        }
    }

    public override async Task<bool> DeleteFileAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            // First, find the movie that contains this file
            var movies = await GetAsync<RadarrMovie[]>("/api/v3/movie", ct);
            if (movies == null)
            {
                Log.Warning("Failed to retrieve movies from Radarr instance '{InstanceName}'", _instanceName);
                return false;
            }

            // Find the movie that matches the file path
            var targetMovie = movies.FirstOrDefault(m => 
                !string.IsNullOrEmpty(m.MovieFile?.Path) && 
                Path.GetFullPath(m.MovieFile.Path).Equals(Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));

            if (targetMovie == null)
            {
                Log.Warning("Could not find movie with file path '{FilePath}' in Radarr instance '{InstanceName}'", 
                    filePath, _instanceName);
                return false;
            }

            // Delete the movie file
            if (targetMovie.MovieFile?.Id != null)
            {
                var deleteEndpoint = $"/api/v3/moviefile/{targetMovie.MovieFile.Id}";
                var success = await DeleteAsync(deleteEndpoint, ct);
                
                if (success)
                {
                    Log.Information("Successfully deleted movie file '{FilePath}' (ID: {FileId}) from Radarr instance '{InstanceName}'", 
                        filePath, targetMovie.MovieFile.Id, _instanceName);
                    return true;
                }
                else
                {
                    Log.Warning("Failed to delete movie file '{FilePath}' (ID: {FileId}) from Radarr instance '{InstanceName}'", 
                        filePath, targetMovie.MovieFile.Id, _instanceName);
                    return false;
                }
            }

            Log.Warning("Movie found but no file ID available for '{FilePath}' in Radarr instance '{InstanceName}'", 
                filePath, _instanceName);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting file '{FilePath}' from Radarr instance '{InstanceName}'", 
                filePath, _instanceName);
            return false;
        }
    }

    public async Task<List<RadarrMovie>> GetMoviesAsync(CancellationToken ct = default)
    {
        var movies = await GetAsync<RadarrMovie[]>("/api/v3/movie", ct);
        return movies?.ToList() ?? new List<RadarrMovie>();
    }
}

public class RadarrSystemStatus
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("buildTime")]
    public DateTime BuildTime { get; set; }

    [JsonPropertyName("isDebug")]
    public bool IsDebug { get; set; }

    [JsonPropertyName("isProduction")]
    public bool IsProduction { get; set; }

    [JsonPropertyName("isAdmin")]
    public bool IsAdmin { get; set; }

    [JsonPropertyName("isUserInteractive")]
    public bool IsUserInteractive { get; set; }

    [JsonPropertyName("startupPath")]
    public string StartupPath { get; set; } = string.Empty;

    [JsonPropertyName("appData")]
    public string AppData { get; set; } = string.Empty;
}

public class RadarrMovie
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("originalTitle")]
    public string OriginalTitle { get; set; } = string.Empty;

    [JsonPropertyName("sortTitle")]
    public string SortTitle { get; set; } = string.Empty;

    [JsonPropertyName("tmdbId")]
    public int TmdbId { get; set; }

    [JsonPropertyName("imdbId")]
    public string ImdbId { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("movieFile")]
    public RadarrMovieFile? MovieFile { get; set; }

    [JsonPropertyName("monitored")]
    public bool Monitored { get; set; }

    [JsonPropertyName("hasFile")]
    public bool HasFile { get; set; }
}

public class RadarrMovieFile
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("movieId")]
    public int MovieId { get; set; }

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("dateAdded")]
    public DateTime DateAdded { get; set; }

    [JsonPropertyName("quality")]
    public RadarrQuality? Quality { get; set; }

    [JsonPropertyName("mediaInfo")]
    public RadarrMediaInfo? MediaInfo { get; set; }
}

public class RadarrQuality
{
    [JsonPropertyName("quality")]
    public RadarrQualityInfo? QualityInfo { get; set; }

    [JsonPropertyName("revision")]
    public RadarrRevision? Revision { get; set; }
}

public class RadarrQualityInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class RadarrRevision
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("real")]
    public int Real { get; set; }

    [JsonPropertyName("isRepack")]
    public bool IsRepack { get; set; }
}

public class RadarrMediaInfo
{
    [JsonPropertyName("videoCodec")]
    public string VideoCodec { get; set; } = string.Empty;

    [JsonPropertyName("audioCodec")]
    public string AudioCodec { get; set; } = string.Empty;

    [JsonPropertyName("audioChannels")]
    public double AudioChannels { get; set; }

    [JsonPropertyName("audioLanguages")]
    public string AudioLanguages { get; set; } = string.Empty;

    [JsonPropertyName("subtitles")]
    public string Subtitles { get; set; } = string.Empty;
}
