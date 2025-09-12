using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class MediaIntegrityService : IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    
    private static Task? _runningTask;
    private static readonly SemaphoreSlim StaticSemaphore = new(1, 1);

    public MediaIntegrityService(
        ConfigManager configManager,
        WebsocketManager websocketManager
    )
    {
        _configManager = configManager;
        _websocketManager = websocketManager;
        _cancellationTokenSource = CancellationTokenSource
            .CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
        
        // Start background integrity checking if enabled
        if (_configManager.IsIntegrityCheckingEnabled())
        {
            _ = StartBackgroundIntegrityCheckAsync(_cancellationTokenSource.Token);
        }
    }

    public async Task<bool> TriggerManualIntegrityCheckAsync()
    {
        await StaticSemaphore.WaitAsync();
        Task? task;
        try
        {
            // if the task is already running, return immediately.
            if (_runningTask is { IsCompleted: false })
                return false;

            // otherwise, run the task.
            _runningTask = Task.Run(() => PerformIntegrityCheckAsync(_cancellationTokenSource.Token));
            task = _runningTask;
        }
        finally
        {
            StaticSemaphore.Release();
        }

        // and wait for it to finish.
        await task;
        return true;
    }

    private async Task StartBackgroundIntegrityCheckAsync(CancellationToken ct)
    {
        var checkIntervalHours = _configManager.GetIntegrityCheckIntervalHours();
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(checkIntervalHours), ct);
                
                await StaticSemaphore.WaitAsync(ct);
                try
                {
                    if (_runningTask is { IsCompleted: false })
                        continue; // Skip if already running
                        
                    _runningTask = Task.Run(() => PerformIntegrityCheckAsync(ct), ct);
                    await _runningTask;
                }
                finally
                {
                    StaticSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in background integrity check");
                // Wait before retrying on error
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
            }
        }
    }

    private async Task PerformIntegrityCheckAsync(CancellationToken ct)
    {
        try
        {
            Log.Information("Starting media integrity check");
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, "starting");

            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            // Get all media files that need checking
            var mediaFiles = await GetMediaFilesToCheckAsync(dbClient, ct);
            
            if (mediaFiles.Count == 0)
            {
                Log.Information("No media files found to check");
                _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, "complete: 0/0");
                return;
            }

            var totalFiles = mediaFiles.Count;
            var processedFiles = 0;
            var corruptFiles = 0;

            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));

            foreach (var davItem in mediaFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var filePath = DatabaseStoreSymlinkFile.GetTargetPath(davItem, _configManager.GetRcloneMountDir());
                    var isCorrupt = await CheckFileIntegrityAsync(filePath, ct);
                    
                    if (isCorrupt)
                    {
                        corruptFiles++;
                        Log.Warning("Corrupt media file detected: {FilePath}", filePath);
                        await HandleCorruptFileAsync(dbClient, davItem, filePath, ct);
                    }

                    // Update integrity check timestamp
                    await UpdateLastIntegrityCheckAsync(dbClient, davItem, !isCorrupt, ct);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error checking integrity of file: {ItemName}", davItem.Name);
                }

                processedFiles++;
                var progress = $"{processedFiles}/{totalFiles} ({corruptFiles} corrupt)";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, progress));
            }

            var finalReport = $"complete: {processedFiles}/{totalFiles} checked, {corruptFiles} corrupt files found";
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, finalReport);
            Log.Information("Media integrity check completed: {Report}", finalReport);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Media integrity check was cancelled");
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, "cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during media integrity check");
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"failed: {ex.Message}");
        }
    }

    private async Task<List<DavItem>> GetMediaFilesToCheckAsync(DavDatabaseClient dbClient, CancellationToken ct)
    {
        var cutoffTime = DateTime.Now.AddDays(-_configManager.GetIntegrityCheckIntervalDays());
        
        // Get all media files (NzbFile and RarFile types) that haven't been checked recently
        var query = dbClient.Ctx.Items
            .Where(item => (item.Type == DavItem.ItemType.NzbFile || item.Type == DavItem.ItemType.RarFile))
            .Where(item => item.FileSize > 0); // Only check files with actual content

        // If we have integrity check records, filter by last check time
        if (await dbClient.Ctx.ConfigItems.AnyAsync(c => c.ConfigName.StartsWith("integrity.last_check."), ct))
        {
            var recentlyCheckedIds = await dbClient.Ctx.ConfigItems
                .Where(c => c.ConfigName.StartsWith("integrity.last_check."))
                .Where(c => DateTime.Parse(c.ConfigValue) > cutoffTime)
                .Select(c => Guid.Parse(c.ConfigName.Substring("integrity.last_check.".Length)))
                .ToListAsync(ct);
                
            query = query.Where(item => !recentlyCheckedIds.Contains(item.Id));
        }

        return await query.Take(_configManager.GetMaxFilesToCheckPerRun()).ToListAsync(ct);
    }

    private async Task<bool> CheckFileIntegrityAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            Log.Warning("File not found for integrity check: {FilePath}", filePath);
            return true; // Consider missing files as corrupt
        }

        var fileExtension = Path.GetExtension(filePath).ToLowerInvariant();
        
        // Check if it's a media file type we can verify
        if (!IsMediaFile(fileExtension))
        {
            return false; // Not a media file, consider it valid
        }

        try
        {
            // Use ffprobe to check media integrity
            var ffprobeArgs = $"-v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 \"{filePath}\"";
            
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = ffprobeArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            
            await process.WaitForExitAsync(ct);
            
            var output = await outputTask;
            var error = await errorTask;

            // If ffprobe exits with error code or produces error output, file is likely corrupt
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error))
            {
                Log.Debug("ffprobe detected issues with {FilePath}: Exit code {ExitCode}, Error: {Error}", 
                    filePath, process.ExitCode, error);
                return true; // File is corrupt
            }

            // If we get a valid codec name in output, file is likely good
            return string.IsNullOrWhiteSpace(output);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error running ffprobe on {FilePath}", filePath);
            return false; // Can't determine, assume file is ok
        }
    }

    private static bool IsMediaFile(string extension)
    {
        var mediaExtensions = new[]
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
            ".mpg", ".mpeg", ".m2ts", ".ts", ".mts", ".vob", ".3gp", ".f4v",
            ".mp3", ".flac", ".aac", ".ogg", ".wma", ".wav", ".m4a"
        };
        
        return mediaExtensions.Contains(extension);
    }

    private async Task HandleCorruptFileAsync(DavDatabaseClient dbClient, DavItem davItem, string filePath, CancellationToken ct)
    {
        var action = _configManager.GetCorruptFileAction();
        
        switch (action.ToLowerInvariant())
        {
            case "delete":
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        Log.Information("Deleted corrupt file: {FilePath}", filePath);
                    }
                    
                    // Remove from database
                    dbClient.Ctx.Items.Remove(davItem);
                    await dbClient.Ctx.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error deleting corrupt file: {FilePath}", filePath);
                }
                break;
                
            case "quarantine":
                try
                {
                    var quarantineDir = Path.Join(_configManager.GetRcloneMountDir(), "quarantine");
                    Directory.CreateDirectory(quarantineDir);
                    
                    var quarantinePath = Path.Join(quarantineDir, Path.GetFileName(filePath));
                    if (File.Exists(filePath))
                    {
                        File.Move(filePath, quarantinePath);
                        Log.Information("Moved corrupt file to quarantine: {FilePath} -> {QuarantinePath}", filePath, quarantinePath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error quarantining corrupt file: {FilePath}", filePath);
                }
                break;
                
            case "log":
            default:
                // Just log the issue (already done above)
                break;
        }
    }

    private async Task UpdateLastIntegrityCheckAsync(DavDatabaseClient dbClient, DavItem davItem, bool isValid, CancellationToken ct)
    {
        var configName = $"integrity.last_check.{davItem.Id}";
        var configValue = DateTime.Now.ToString("O");
        
        var existingConfig = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == configName, ct);
            
        if (existingConfig != null)
        {
            existingConfig.ConfigValue = configValue;
        }
        else
        {
            dbClient.Ctx.ConfigItems.Add(new ConfigItem
            {
                ConfigName = configName,
                ConfigValue = configValue
            });
        }

        // Also store validation result
        var statusConfigName = $"integrity.status.{davItem.Id}";
        var statusConfig = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == statusConfigName, ct);
            
        if (statusConfig != null)
        {
            statusConfig.ConfigValue = isValid ? "valid" : "corrupt";
        }
        else
        {
            dbClient.Ctx.ConfigItems.Add(new ConfigItem
            {
                ConfigName = statusConfigName,
                ConfigValue = isValid ? "valid" : "corrupt"
            });
        }

        await dbClient.Ctx.SaveChangesAsync(ct);
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _semaphore?.Dispose();
    }
}
