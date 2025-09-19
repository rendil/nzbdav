using System.Text.Json.Serialization;
using Serilog;

namespace NzbWebDAV.Clients;

public class SonarrClient : ArrClient
{
    public SonarrClient(string baseUrl, string apiKey, string instanceName) 
        : base(baseUrl, apiKey, instanceName)
    {
    }

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await GetAsync<SonarrSystemStatus>("/api/v3/system/status", ct);
            if (status != null)
            {
                Log.Information("Successfully connected to Sonarr instance '{InstanceName}' (v{Version})", 
                    _instanceName, status.Version);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to test connection to Sonarr instance '{InstanceName}'", _instanceName);
            return false;
        }
    }

    public override async Task<bool> DeleteFileAsync(string filePath, CancellationToken ct = default)
    {
        var (success, seriesId) = await DeleteFileWithSeriesIdAsync(filePath, ct);
        return success;
    }

    public async Task<(bool Success, int[]? EpisodeIds)> DeleteFileWithEpisodeIdsAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            // First, find the episode file that matches this path
            var episodeFiles = await GetAsync<SonarrEpisodeFile[]>("/api/v3/episodefile", ct);
            if (episodeFiles == null)
            {
                Log.Debug("Failed to retrieve episode files from Sonarr instance '{InstanceName}' - this is normal if the file is a movie", _instanceName);
                return (false, null);
            }

            // Try multiple matching strategies in order of preference
            var targetEpisodeFile = FindEpisodeFileByPath(episodeFiles, filePath);

            if (targetEpisodeFile == null)
            {
                Log.Warning("Could not find episode file with path '{FilePath}' in Sonarr instance '{InstanceName}' using any matching strategy", 
                    filePath, _instanceName);
                return (false, null);
            }

            // Get the episodes associated with this episode file
            var episodes = await GetAsync<SonarrEpisode[]>($"/api/v3/episode?episodeFileId={targetEpisodeFile.Id}", ct);
            var episodeIds = episodes?.Select(e => e.Id).ToArray();

            // Delete the episode file
            var deleteEndpoint = $"/api/v3/episodefile/{targetEpisodeFile.Id}";
            var success = await DeleteAsync(deleteEndpoint, ct);
            
            if (success)
            {
                Log.Information("Successfully deleted episode file '{FilePath}' (ID: {FileId}) containing {EpisodeCount} episodes from Sonarr instance '{InstanceName}'", 
                    filePath, targetEpisodeFile.Id, episodeIds?.Length ?? 0, _instanceName);
                return (true, episodeIds);
            }
            else
            {
                Log.Warning("Failed to delete episode file '{FilePath}' (ID: {FileId}) from Sonarr instance '{InstanceName}'", 
                    filePath, targetEpisodeFile.Id, _instanceName);
                return (false, episodeIds);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting file '{FilePath}' from Sonarr instance '{InstanceName}'", 
                filePath, _instanceName);
            return (false, null);
        }
    }

    // Keep the old method for backward compatibility, but update it to use the new method
    public async Task<(bool Success, int? SeriesId)> DeleteFileWithSeriesIdAsync(string filePath, CancellationToken ct = default)
    {
        var (success, episodeIds) = await DeleteFileWithEpisodeIdsAsync(filePath, ct);
        
        // If we have episode IDs, get the series ID from the first episode
        if (episodeIds?.Length > 0)
        {
            try
            {
                var episode = await GetAsync<SonarrEpisode>($"/api/v3/episode/{episodeIds[0]}", ct);
                return (success, episode?.SeriesId);
            }
            catch
            {
                return (success, null);
            }
        }
        
        return (success, null);
    }

    public async Task<List<SonarrSeries>> GetSeriesAsync(CancellationToken ct = default)
    {
        var series = await GetAsync<SonarrSeries[]>("/api/v3/series", ct);
        return series?.ToList() ?? new List<SonarrSeries>();
    }

    public async Task<bool> SearchForEpisodeAsync(int episodeId, CancellationToken ct = default)
    {
        return await SearchForEpisodesAsync(new[] { episodeId }, ct);
    }

    public async Task<bool> SearchForEpisodesAsync(int[] episodeIds, CancellationToken ct = default)
    {
        try
        {
            var command = new
            {
                name = "EpisodeSearch",
                episodeIds = episodeIds
            };

            var result = await PostAsync<object>("/api/v3/command", command, ct);
            if (result != null)
            {
                Log.Information("Successfully triggered search for {EpisodeCount} episode(s) (IDs: {EpisodeIds}) in Sonarr instance '{InstanceName}'", 
                    episodeIds.Length, string.Join(", ", episodeIds), _instanceName);
                return true;
            }

            Log.Warning("Failed to trigger search for {EpisodeCount} episode(s) (IDs: {EpisodeIds}) in Sonarr instance '{InstanceName}' - no response", 
                episodeIds.Length, string.Join(", ", episodeIds), _instanceName);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error triggering search for {EpisodeCount} episode(s) (IDs: {EpisodeIds}) in Sonarr instance '{InstanceName}'", 
                episodeIds.Length, string.Join(", ", episodeIds), _instanceName);
            return false;
        }
    }

    public async Task<bool> SearchForSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        try
        {
            var command = new
            {
                name = "SeriesSearch",
                seriesId = seriesId
            };

            var result = await PostAsync<object>("/api/v3/command", command, ct);
            if (result != null)
            {
                Log.Information("Successfully triggered search for series ID {SeriesId} in Sonarr instance '{InstanceName}'", 
                    seriesId, _instanceName);
                return true;
            }

            Log.Warning("Failed to trigger search for series ID {SeriesId} in Sonarr instance '{InstanceName}' - no response", 
                seriesId, _instanceName);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error triggering search for series ID {SeriesId} in Sonarr instance '{InstanceName}'", 
                seriesId, _instanceName);
            return false;
        }
    }

    public async Task<bool> UnmonitorSeriesAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            // First, find the episode file
            var episodeFiles = await GetEpisodeFilesAsync(ct);
            var targetEpisodeFile = FindEpisodeFileByPath(episodeFiles.ToArray(), filePath);
            
            if (targetEpisodeFile == null)
            {
                Log.Warning("Could not find episode file with path '{FilePath}' in Sonarr instance '{InstanceName}' for unmonitoring", 
                    filePath, _instanceName);
                return false;
            }

            // Get the series for this episode file
            var series = await GetAsync<SonarrSeries[]>("/api/v3/series", ct);
            if (series == null)
            {
                Log.Warning("Failed to retrieve series from Sonarr instance '{InstanceName}' for unmonitoring", _instanceName);
                return false;
            }

            var targetSeries = series.FirstOrDefault(s => s.Id == targetEpisodeFile.SeriesId);
            if (targetSeries == null)
            {
                Log.Warning("Could not find series (ID: {SeriesId}) for episode file '{FilePath}' in Sonarr instance '{InstanceName}' for unmonitoring", 
                    targetEpisodeFile.SeriesId, filePath, _instanceName);
                return false;
            }

            // Only unmonitor if currently monitored
            if (!targetSeries.Monitored)
            {
                Log.Debug("Series '{Title}' is already unmonitored in Sonarr instance '{InstanceName}'", 
                    targetSeries.Title, _instanceName);
                return true; // Already unmonitored, consider this success
            }

            // Update the series to unmonitor it
            var updatePayload = new
            {
                id = targetSeries.Id,
                monitored = false,
                // Include other required fields to avoid API errors
                title = targetSeries.Title,
                titleSlug = targetSeries.TitleSlug,
                tvdbId = targetSeries.TvdbId,
                year = targetSeries.Year,
                path = targetSeries.Path,
                seasons = targetSeries.Seasons
            };

            var result = await PutAsync<object>($"/api/v3/series/{targetSeries.Id}", updatePayload, ct);
            if (result != null)
            {
                Log.Information("Successfully unmonitored series '{Title}' (ID: {SeriesId}) in Sonarr instance '{InstanceName}' after integrity check", 
                    targetSeries.Title, targetSeries.Id, _instanceName);
                return true;
            }

            Log.Warning("Failed to unmonitor series '{Title}' (ID: {SeriesId}) in Sonarr instance '{InstanceName}' - no response", 
                targetSeries.Title, targetSeries.Id, _instanceName);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error unmonitoring series for file '{FilePath}' in Sonarr instance '{InstanceName}'", 
                filePath, _instanceName);
            return false;
        }
    }

    public async Task<bool> MonitorSeriesAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            // First, find the episode file
            var episodeFiles = await GetEpisodeFilesAsync(ct);
            var targetEpisodeFile = FindEpisodeFileByPath(episodeFiles.ToArray(), filePath);
            
            if (targetEpisodeFile == null)
            {
                Log.Warning("Could not find episode file with path '{FilePath}' in Sonarr instance '{InstanceName}' for monitoring", 
                    filePath, _instanceName);
                return false;
            }

            // Get the series for this episode file
            var series = await GetAsync<SonarrSeries[]>("/api/v3/series", ct);
            if (series == null)
            {
                Log.Warning("Failed to retrieve series from Sonarr instance '{InstanceName}' for monitoring", _instanceName);
                return false;
            }

            var targetSeries = series.FirstOrDefault(s => s.Id == targetEpisodeFile.SeriesId);
            if (targetSeries == null)
            {
                Log.Warning("Could not find series (ID: {SeriesId}) for episode file '{FilePath}' in Sonarr instance '{InstanceName}' for monitoring", 
                    targetEpisodeFile.SeriesId, filePath, _instanceName);
                return false;
            }

            // Only monitor if currently unmonitored
            if (targetSeries.Monitored)
            {
                Log.Debug("Series '{Title}' is already monitored in Sonarr instance '{InstanceName}'", 
                    targetSeries.Title, _instanceName);
                return true; // Already monitored, consider this success
            }

            // Update the series to monitor it
            var updatePayload = new
            {
                id = targetSeries.Id,
                monitored = true,
                // Include other required fields to avoid API errors
                title = targetSeries.Title,
                titleSlug = targetSeries.TitleSlug,
                tvdbId = targetSeries.TvdbId,
                year = targetSeries.Year,
                path = targetSeries.Path,
                seasons = targetSeries.Seasons
            };

            var result = await PutAsync<object>($"/api/v3/series/{targetSeries.Id}", updatePayload, ct);
            if (result != null)
            {
                Log.Information("Successfully monitored series '{Title}' (ID: {SeriesId}) in Sonarr instance '{InstanceName}' for re-download after corruption", 
                    targetSeries.Title, targetSeries.Id, _instanceName);
                return true;
            }

            Log.Warning("Failed to monitor series '{Title}' (ID: {SeriesId}) in Sonarr instance '{InstanceName}' - no response", 
                targetSeries.Title, targetSeries.Id, _instanceName);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error monitoring series for file '{FilePath}' in Sonarr instance '{InstanceName}'", 
                filePath, _instanceName);
            return false;
        }
    }

    public async Task<List<SonarrEpisodeFile>> GetEpisodeFilesAsync(CancellationToken ct = default)
    {
        var episodeFiles = await GetAsync<SonarrEpisodeFile[]>("/api/v3/episodefile", ct);
        return episodeFiles?.ToList() ?? new List<SonarrEpisodeFile>();
    }

    private SonarrEpisodeFile? FindEpisodeFileByPath(SonarrEpisodeFile[] episodeFiles, string filePath)
    {
        // Strategy 1: Exact full path match (most reliable)
        var exactMatch = episodeFiles.FirstOrDefault(ef => 
            !string.IsNullOrEmpty(ef.Path) && 
            string.Equals(ef.Path, filePath, StringComparison.OrdinalIgnoreCase));
        
        if (exactMatch != null)
        {
            Log.Debug("Found episode file by exact path match: ID {FileId} -> '{FilePath}'", exactMatch.Id, exactMatch.Path);
            return exactMatch;
        }

        // Strategy 2: Normalized full path match (handles different path formats)
        var normalizedMatch = episodeFiles.FirstOrDefault(ef => 
            !string.IsNullOrEmpty(ef.Path) && 
            NormalizePath(ef.Path).Equals(NormalizePath(filePath), StringComparison.OrdinalIgnoreCase));
        
        if (normalizedMatch != null)
        {
            Log.Debug("Found episode file by normalized path match: ID {FileId} -> '{FilePath}'", normalizedMatch.Id, normalizedMatch.Path);
            return normalizedMatch;
        }

        // Strategy 3: Filename-only match (fallback for different mount points)
        var fileName = Path.GetFileName(filePath);
        var filenameMatch = episodeFiles.FirstOrDefault(ef => 
            !string.IsNullOrEmpty(ef.Path) && 
            string.Equals(Path.GetFileName(ef.Path), fileName, StringComparison.OrdinalIgnoreCase));
        
        if (filenameMatch != null)
        {
            Log.Debug("Found episode file by filename match: ID {FileId} -> '{FilePath}' (filename: '{FileName}')", 
                filenameMatch.Id, filenameMatch.Path, fileName);
            return filenameMatch;
        }

        // Strategy 4: Relative path match (handles different root directories)
        var relativePath = GetRelativePath(filePath);
        if (!string.IsNullOrEmpty(relativePath))
        {
            var relativeMatch = episodeFiles.FirstOrDefault(ef => 
                !string.IsNullOrEmpty(ef.Path) && 
                GetRelativePath(ef.Path).Equals(relativePath, StringComparison.OrdinalIgnoreCase));
            
            if (relativeMatch != null)
            {
                Log.Debug("Found episode file by relative path match: ID {FileId} -> '{FilePath}' (relative: '{RelativePath}')", 
                    relativeMatch.Id, relativeMatch.Path, relativePath);
                return relativeMatch;
            }
        }

        Log.Debug("No episode file found for '{FilePath}' using any matching strategy", filePath);
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
        
        // Common patterns for TV shows: /tv/, /shows/, /series/, /content/tv/...
        var patterns = new[] { "/tv/", "/shows/", "/series/", "/content/", "/media/", "/data/" };
        
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

public class SonarrSystemStatus
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

public class SonarrSeries
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("sortTitle")]
    public string SortTitle { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = string.Empty;

    [JsonPropertyName("network")]
    public string Network { get; set; } = string.Empty;

    [JsonPropertyName("airTime")]
    public string AirTime { get; set; } = string.Empty;

    [JsonPropertyName("images")]
    public SonarrImage[]? Images { get; set; }

    [JsonPropertyName("seasons")]
    public SonarrSeason[]? Seasons { get; set; }

    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("tvdbId")]
    public int TvdbId { get; set; }

    [JsonPropertyName("tvMazeId")]
    public int TvMazeId { get; set; }

    [JsonPropertyName("imdbId")]
    public string ImdbId { get; set; } = string.Empty;

    [JsonPropertyName("titleSlug")]
    public string TitleSlug { get; set; } = string.Empty;

    [JsonPropertyName("monitored")]
    public bool Monitored { get; set; }

    [JsonPropertyName("useSceneNumbering")]
    public bool UseSceneNumbering { get; set; }

    [JsonPropertyName("runtime")]
    public int Runtime { get; set; }

    [JsonPropertyName("seriesType")]
    public string SeriesType { get; set; } = string.Empty;

    [JsonPropertyName("cleanTitle")]
    public string CleanTitle { get; set; } = string.Empty;

    [JsonPropertyName("languageProfileId")]
    public int LanguageProfileId { get; set; }

    [JsonPropertyName("genres")]
    public string[]? Genres { get; set; }

    [JsonPropertyName("tags")]
    public int[]? Tags { get; set; }

    [JsonPropertyName("added")]
    public DateTime Added { get; set; }

    [JsonPropertyName("statistics")]
    public SonarrSeriesStatistics? Statistics { get; set; }
}

public class SonarrEpisode
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("seriesId")]
    public int SeriesId { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("episodeNumber")]
    public int EpisodeNumber { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("hasFile")]
    public bool HasFile { get; set; }

    [JsonPropertyName("episodeFileId")]
    public int? EpisodeFileId { get; set; }
}

public class SonarrEpisodeFile
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("seriesId")]
    public int SeriesId { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("dateAdded")]
    public DateTime DateAdded { get; set; }

    [JsonPropertyName("quality")]
    public SonarrQuality? Quality { get; set; }

    [JsonPropertyName("mediaInfo")]
    public SonarrMediaInfo? MediaInfo { get; set; }

    [JsonPropertyName("originalFilePath")]
    public string OriginalFilePath { get; set; } = string.Empty;

    [JsonPropertyName("sceneName")]
    public string SceneName { get; set; } = string.Empty;

    [JsonPropertyName("releaseGroup")]
    public string ReleaseGroup { get; set; } = string.Empty;

    [JsonPropertyName("edition")]
    public string Edition { get; set; } = string.Empty;

    [JsonPropertyName("languages")]
    public SonarrLanguage[]? Languages { get; set; }
}

public class SonarrImage
{
    [JsonPropertyName("coverType")]
    public string CoverType { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("remoteUrl")]
    public string RemoteUrl { get; set; } = string.Empty;
}

public class SonarrSeason
{
    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("monitored")]
    public bool Monitored { get; set; }

    [JsonPropertyName("statistics")]
    public SonarrSeasonStatistics? Statistics { get; set; }
}

public class SonarrSeriesStatistics
{
    [JsonPropertyName("seasonCount")]
    public int SeasonCount { get; set; }

    [JsonPropertyName("episodeFileCount")]
    public int EpisodeFileCount { get; set; }

    [JsonPropertyName("episodeCount")]
    public int EpisodeCount { get; set; }

    [JsonPropertyName("totalEpisodeCount")]
    public int TotalEpisodeCount { get; set; }

    [JsonPropertyName("sizeOnDisk")]
    public long SizeOnDisk { get; set; }

    [JsonPropertyName("percentOfEpisodes")]
    public double PercentOfEpisodes { get; set; }
}

public class SonarrSeasonStatistics
{
    [JsonPropertyName("episodeFileCount")]
    public int EpisodeFileCount { get; set; }

    [JsonPropertyName("episodeCount")]
    public int EpisodeCount { get; set; }

    [JsonPropertyName("totalEpisodeCount")]
    public int TotalEpisodeCount { get; set; }

    [JsonPropertyName("sizeOnDisk")]
    public long SizeOnDisk { get; set; }

    [JsonPropertyName("percentOfEpisodes")]
    public double PercentOfEpisodes { get; set; }
}

public class SonarrQuality
{
    [JsonPropertyName("quality")]
    public SonarrQualityInfo? QualityInfo { get; set; }

    [JsonPropertyName("revision")]
    public SonarrRevision? Revision { get; set; }
}

public class SonarrQualityInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("resolution")]
    public int Resolution { get; set; }
}

public class SonarrRevision
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("real")]
    public int Real { get; set; }

    [JsonPropertyName("isRepack")]
    public bool IsRepack { get; set; }
}

public class SonarrMediaInfo
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

    [JsonPropertyName("runTime")]
    public string RunTime { get; set; } = string.Empty;
}

public class SonarrLanguage
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
