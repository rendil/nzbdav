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
        var (success, movieId) = await DeleteFileWithMovieIdAsync(filePath, ct);
        return success;
    }

    public async Task<(bool Success, int? MovieId)> DeleteFileWithMovieIdAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            // First, find the movie that contains this file
            var movies = await GetAsync<RadarrMovie[]>("/api/v3/movie", ct);
            if (movies == null)
            {
                Log.Warning("Failed to retrieve movies from Radarr instance '{InstanceName}'", _instanceName);
                return (false, null);
            }

            // Log all movie file paths for debugging
            Log.Debug("Searching for file '{FilePath}' in {MovieCount} movies from Radarr instance '{InstanceName}'", 
                filePath, movies.Length, _instanceName);
            
            foreach (var movie in movies.Take(3)) // Log first 3 for debugging
            {
                Log.Debug("Movie: '{Title}', File path: '{MovieFilePath}'", 
                    movie.Title, movie.MovieFile?.Path ?? "No file");
            }

            // Try multiple matching strategies in order of preference
            var targetMovie = FindMovieByPath(movies, filePath);

            if (targetMovie == null)
            {
                Log.Warning("Could not find movie with file path '{FilePath}' in Radarr instance '{InstanceName}' using any matching strategy", 
                    filePath, _instanceName);
                return (false, null);
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
                    return (true, targetMovie.Id);
                }
                else
                {
                    Log.Warning("Failed to delete movie file '{FilePath}' (ID: {FileId}) from Radarr instance '{InstanceName}'", 
                        filePath, targetMovie.MovieFile.Id, _instanceName);
                    return (false, targetMovie.Id);
                }
            }

            Log.Warning("Movie found but no file ID available for '{FilePath}' in Radarr instance '{InstanceName}'", 
                filePath, _instanceName);
            return (false, targetMovie.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting file '{FilePath}' from Radarr instance '{InstanceName}'", 
                filePath, _instanceName);
            return (false, null);
        }
    }

    public async Task<List<RadarrMovie>> GetMoviesAsync(CancellationToken ct = default)
    {
        var movies = await GetAsync<RadarrMovie[]>("/api/v3/movie", ct);
        return movies?.ToList() ?? new List<RadarrMovie>();
    }

    public async Task<bool> SearchForMovieAsync(int movieId, CancellationToken ct = default)
    {
        try
        {
            var command = new
            {
                name = "MoviesSearch",
                movieIds = new[] { movieId }
            };

            var result = await PostAsync<object>("/api/v3/command", command, ct);
            if (result != null)
            {
                Log.Information("Successfully triggered search for movie ID {MovieId} in Radarr instance '{InstanceName}'", 
                    movieId, _instanceName);
                return true;
            }

            Log.Warning("Failed to trigger search for movie ID {MovieId} in Radarr instance '{InstanceName}' - no response", 
                movieId, _instanceName);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error triggering search for movie ID {MovieId} in Radarr instance '{InstanceName}'", 
                movieId, _instanceName);
            return false;
        }
    }

    public async Task<bool> UnmonitorMovieAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            // First, find the movie that contains this file
            var movies = await GetAsync<RadarrMovie[]>("/api/v3/movie", ct);
            if (movies == null)
            {
                Log.Warning("Failed to retrieve movies from Radarr instance '{InstanceName}' for unmonitoring", _instanceName);
                return false;
            }

            var targetMovie = FindMovieByPath(movies, filePath);
            if (targetMovie == null)
            {
                Log.Warning("Could not find movie with file path '{FilePath}' in Radarr instance '{InstanceName}' for unmonitoring", 
                    filePath, _instanceName);
                return false;
            }

            // Only unmonitor if currently monitored
            if (!targetMovie.Monitored)
            {
                Log.Debug("Movie '{Title}' is already unmonitored in Radarr instance '{InstanceName}'", 
                    targetMovie.Title, _instanceName);
                return true; // Already unmonitored, consider this success
            }

            // Update the movie to unmonitor it
            var updatePayload = new
            {
                id = targetMovie.Id,
                monitored = false,
                // Include other required fields to avoid API errors
                title = targetMovie.Title,
                tmdbId = targetMovie.TmdbId,
                year = targetMovie.Year,
                path = targetMovie.Path
            };

            var result = await PutAsync<object>($"/api/v3/movie/{targetMovie.Id}", updatePayload, ct);
            if (result != null)
            {
                Log.Information("Successfully unmonitored movie '{Title}' (ID: {MovieId}) in Radarr instance '{InstanceName}' after integrity check", 
                    targetMovie.Title, targetMovie.Id, _instanceName);
                return true;
            }

            Log.Warning("Failed to unmonitor movie '{Title}' (ID: {MovieId}) in Radarr instance '{InstanceName}' - no response", 
                targetMovie.Title, targetMovie.Id, _instanceName);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error unmonitoring movie for file '{FilePath}' in Radarr instance '{InstanceName}'", 
                filePath, _instanceName);
            return false;
        }
    }

    private RadarrMovie? FindMovieByPath(RadarrMovie[] movies, string filePath)
    {
        // Strategy 1: Exact full path match (most reliable)
        var exactMatch = movies.FirstOrDefault(m => 
            !string.IsNullOrEmpty(m.MovieFile?.Path) && 
            string.Equals(m.MovieFile.Path, filePath, StringComparison.OrdinalIgnoreCase));
        
        if (exactMatch != null)
        {
            Log.Debug("Found movie by exact path match: '{Title}' -> '{FilePath}'", exactMatch.Title, exactMatch.MovieFile?.Path);
            return exactMatch;
        }

        // Strategy 2: Normalized full path match (handles different path formats)
        var normalizedMatch = movies.FirstOrDefault(m => 
            !string.IsNullOrEmpty(m.MovieFile?.Path) && 
            NormalizePath(m.MovieFile.Path).Equals(NormalizePath(filePath), StringComparison.OrdinalIgnoreCase));
        
        if (normalizedMatch != null)
        {
            Log.Debug("Found movie by normalized path match: '{Title}' -> '{FilePath}'", normalizedMatch.Title, normalizedMatch.MovieFile?.Path);
            return normalizedMatch;
        }

        // Strategy 3: Filename-only match (fallback for different mount points)
        var fileName = Path.GetFileName(filePath);
        var filenameMatch = movies.FirstOrDefault(m => 
            !string.IsNullOrEmpty(m.MovieFile?.Path) && 
            string.Equals(Path.GetFileName(m.MovieFile.Path), fileName, StringComparison.OrdinalIgnoreCase));
        
        if (filenameMatch != null)
        {
            Log.Debug("Found movie by filename match: '{Title}' -> '{FilePath}' (filename: '{FileName}')", 
                filenameMatch.Title, filenameMatch.MovieFile?.Path, fileName);
            return filenameMatch;
        }

        // Strategy 4: Relative path match (handles different root directories)
        var relativePath = GetRelativePath(filePath);
        if (!string.IsNullOrEmpty(relativePath))
        {
            var relativeMatch = movies.FirstOrDefault(m => 
                !string.IsNullOrEmpty(m.MovieFile?.Path) && 
                GetRelativePath(m.MovieFile.Path).Equals(relativePath, StringComparison.OrdinalIgnoreCase));
            
            if (relativeMatch != null)
            {
                Log.Debug("Found movie by relative path match: '{Title}' -> '{FilePath}' (relative: '{RelativePath}')", 
                    relativeMatch.Title, relativeMatch.MovieFile?.Path, relativePath);
                return relativeMatch;
            }
        }

        Log.Debug("No movie found for file '{FilePath}' using any matching strategy", filePath);
        return null;
    }

    private static string NormalizePath(string path)
    {
        // Normalize path separators and resolve relative components
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        }
        catch
        {
            // If path normalization fails, return the original path with normalized separators
            return path.Replace('\\', '/').TrimEnd('/');
        }
    }

    private static string GetRelativePath(string path)
    {
        // Extract the relative path from common root directories
        var normalizedPath = path.Replace('\\', '/');
        
        // Common patterns: /data/media/movies/..., /movies/..., /content/movies/...
        var patterns = new[] { "/movies/", "/content/", "/media/", "/data/" };
        
        foreach (var pattern in patterns)
        {
            var index = normalizedPath.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return normalizedPath.Substring(index + pattern.Length - 1); // Keep the leading slash
            }
        }
        
        return "";
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
