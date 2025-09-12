using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients;
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
    private readonly ArrManager _arrManager;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    
    private static Task? _runningTask;
    private static readonly SemaphoreSlim StaticSemaphore = new(1, 1);

    public MediaIntegrityService(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ArrManager arrManager
    )
    {
        _configManager = configManager;
        _websocketManager = websocketManager;
        _arrManager = arrManager;
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

            // Check if library directory is configured (required for arr integration)
            var libraryDir = _configManager.GetLibraryDir();
            if (string.IsNullOrEmpty(libraryDir) && _configManager.GetCorruptFileAction() == "delete_via_arr")
            {
                var errorMsg = "Library directory must be configured for Radarr/Sonarr integration";
                Log.Error(errorMsg);
                _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"failed: {errorMsg}");
                return;
            }

            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            // Get media files to check based on configuration
            List<string> filePaths;
            if (!string.IsNullOrEmpty(libraryDir) && Directory.Exists(libraryDir))
            {
                // Use library directory for arr integration
                filePaths = await GetLibraryFilesToCheckAsync(libraryDir, ct);
                Log.Information("Checking files in library directory: {LibraryDir}", libraryDir);
            }
            else
            {
                // Fallback to internal files for non-arr usage
                var davItems = await GetDavItemsToCheckAsync(dbClient, ct);
                filePaths = davItems.Select(item => 
                    DatabaseStoreSymlinkFile.GetTargetPath(item, _configManager.GetRcloneMountDir())).ToList();
                Log.Information("Checking internal nzbdav files");
            }
            
            if (filePaths.Count == 0)
            {
                Log.Information("No media files found to check");
                _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, "complete: 0/0");
                return;
            }

            var totalFiles = filePaths.Count;
            var processedFiles = 0;
            var corruptFiles = 0;

            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));

            foreach (var filePath in filePaths)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var isCorrupt = await CheckFileIntegrityAsync(filePath, ct);
                    
                    if (isCorrupt)
                    {
                        corruptFiles++;
                        Log.Warning("Corrupt media file detected: {FilePath}", filePath);
                        await HandleCorruptFileAsync(dbClient, null, filePath, ct);
                    }

                    // Update integrity check timestamp for library files
                    if (!string.IsNullOrEmpty(libraryDir))
                    {
                        await UpdateLastIntegrityCheckForLibraryFileAsync(dbClient, filePath, !isCorrupt, ct);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error checking integrity of file: {FilePath}", filePath);
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

    private async Task<List<DavItem>> GetDavItemsToCheckAsync(DavDatabaseClient dbClient, CancellationToken ct)
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

    private async Task<List<string>> GetLibraryFilesToCheckAsync(string libraryDir, CancellationToken ct)
    {
        var filePaths = new List<string>();
        var cutoffTime = DateTime.Now.AddDays(-_configManager.GetIntegrityCheckIntervalDays());
        
        try
        {
            // Get all media files in library directory recursively
            var allFiles = Directory.EnumerateFiles(libraryDir, "*", SearchOption.AllDirectories)
                .Where(file => IsMediaFile(Path.GetExtension(file).ToLowerInvariant()))
                .ToList();

            Log.Information("Found {FileCount} media files in library directory", allFiles.Count);

            // Filter out recently checked files
            foreach (var filePath in allFiles)
            {
                ct.ThrowIfCancellationRequested();
                
                var fileHash = GetFilePathHash(filePath);
                var lastCheckConfig = $"integrity.last_check.library.{fileHash}";
                
                using var dbContext = new DavDatabaseContext();
                var lastCheckValue = await dbContext.ConfigItems
                    .Where(c => c.ConfigName == lastCheckConfig)
                    .Select(c => c.ConfigValue)
                    .FirstOrDefaultAsync(ct);

                if (lastCheckValue == null || !DateTime.TryParse(lastCheckValue, out var lastCheck) || lastCheck <= cutoffTime)
                {
                    filePaths.Add(filePath);
                    
                    // Limit the number of files per run
                    if (filePaths.Count >= _configManager.GetMaxFilesToCheckPerRun())
                        break;
                }
            }

            Log.Information("Selected {SelectedCount} files for integrity check", filePaths.Count);
            return filePaths;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error scanning library directory: {LibraryDir}", libraryDir);
            return filePaths;
        }
    }

    private static string GetFilePathHash(string filePath)
    {
        // Create a simple hash of the file path for unique identification
        return BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(filePath))).Replace("-", "")[..16];
    }

    private async Task UpdateLastIntegrityCheckForLibraryFileAsync(DavDatabaseClient dbClient, string filePath, bool isValid, CancellationToken ct)
    {
        var fileHash = GetFilePathHash(filePath);
        var configName = $"integrity.last_check.library.{fileHash}";
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
        var statusConfigName = $"integrity.status.library.{fileHash}";
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

            // Log detailed ffprobe results for debugging
            Log.Debug("ffprobe results for {FilePath}: Exit code {ExitCode}, Output: '{Output}', Error: '{Error}'", 
                filePath, process.ExitCode, output?.Trim(), error?.Trim());

            // If ffprobe exits with error code or produces error output, file is likely corrupt
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error))
            {
                Log.Warning("ffprobe detected issues with {FilePath}: Exit code {ExitCode}, Error: {Error}", 
                    filePath, process.ExitCode, error);
                return true; // File is corrupt
            }

            // If we get a valid codec name in output, file is good; if no output, file is corrupt
            var isCorrupt = string.IsNullOrWhiteSpace(output);
            Log.Debug("File integrity check result for {FilePath}: IsCorrupt={IsCorrupt}, Output='{Output}'", 
                filePath, isCorrupt, output?.Trim());
            return isCorrupt;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error running ffprobe on {FilePath}", filePath);
            
            // If ffprobe is not found or can't be started, this is a configuration issue
            // We should treat this as a problem that needs attention
            if (ex is System.ComponentModel.Win32Exception win32Ex && win32Ex.NativeErrorCode == 2)
            {
                Log.Error("ffprobe not found - please ensure FFmpeg is installed. File integrity cannot be verified: {FilePath}", filePath);
                // Return true (corrupt) to indicate there's an issue that needs attention
                // This prevents silent failures where users think files are checked but they're not
                return true;
            }
            
            // For other errors, assume file is ok but log the issue
            return false;
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

    private async Task HandleCorruptFileAsync(DavDatabaseClient dbClient, DavItem? davItem, string filePath, CancellationToken ct)
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
                    
                    // Remove from database if this is a DavItem
                    if (davItem != null)
                    {
                        dbClient.Ctx.Items.Remove(davItem);
                        await dbClient.Ctx.SaveChangesAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error deleting corrupt file: {FilePath}", filePath);
                }
                break;
                
            case "delete_via_arr":
                try
                {
                    Log.Information("Attempting to delete corrupt file via Radarr/Sonarr: {FilePath}", filePath);
                    var success = await _arrManager.DeleteFileFromArrAsync(filePath, ct);
                    
                    if (success)
                    {
                        Log.Information("Successfully deleted corrupt file via Radarr/Sonarr: {FilePath}", filePath);
                        // Remove from database since the file was deleted via arr (if this is a DavItem)
                        if (davItem != null)
                        {
                            dbClient.Ctx.Items.Remove(davItem);
                            await dbClient.Ctx.SaveChangesAsync(ct);
                        }
                    }
                    else
                    {
                        Log.Warning("Failed to delete corrupt file via Radarr/Sonarr: {FilePath}", filePath);
                        
                        // Check if direct deletion fallback is enabled
                        if (_configManager.IsDirectDeletionFallbackEnabled())
                        {
                            Log.Information("Direct deletion fallback is enabled, deleting file directly: {FilePath}", filePath);
                            // Fallback to direct deletion
                            if (File.Exists(filePath))
                            {
                                File.Delete(filePath);
                                Log.Information("Deleted corrupt file directly as fallback: {FilePath}", filePath);
                            }
                            
                            // Remove from database (if this is a DavItem)
                            if (davItem != null)
                            {
                                dbClient.Ctx.Items.Remove(davItem);
                                await dbClient.Ctx.SaveChangesAsync(ct);
                            }
                        }
                        else
                        {
                            Log.Information("Direct deletion fallback is disabled, leaving corrupt file in place: {FilePath}", filePath);
                            Log.Warning("Corrupt file was not deleted by Radarr/Sonarr and direct deletion fallback is disabled. File remains: {FilePath}", filePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error deleting corrupt file via Radarr/Sonarr: {FilePath}", filePath);
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
