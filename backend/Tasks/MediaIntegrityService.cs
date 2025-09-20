using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class IntegrityCheckItem
{
    public required DavItem DavItem { get; init; }
    public string? LibraryFilePath { get; init; }
}

public class MediaIntegrityService : IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly ArrManager _arrManager;
    private readonly UsenetStreamingClient _usenetClient;
    private CancellationTokenSource _cancellationTokenSource;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private static Task? _runningTask;
    private static readonly SemaphoreSlim StaticSemaphore = new(1, 1);

    public MediaIntegrityService(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ArrManager arrManager,
        UsenetStreamingClient usenetClient
    )
    {
        Log.Information("Initializing MediaIntegrityService");
        _configManager = configManager;
        _websocketManager = websocketManager;
        _arrManager = arrManager;
        _usenetClient = usenetClient;
        _cancellationTokenSource = CancellationTokenSource
            .CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());

        // Start background integrity checking if enabled
        if (_configManager.IsIntegrityCheckingEnabled())
        {
            Log.Information("Integrity checking is enabled, starting background scheduler");
            _ = StartBackgroundIntegrityCheckAsync(_cancellationTokenSource.Token);
        }
        else
        {
            Log.Information("Integrity checking is disabled, background scheduler will not start");
        }
    }

    public async Task<bool> TriggerManualIntegrityCheckAsync()
    {
        await StaticSemaphore.WaitAsync();
        try
        {
            // if the task is already running, return immediately.
            if (_runningTask is { IsCompleted: false })
                return false;

            // otherwise, start the task and return immediately (don't wait for completion).
            _runningTask = Task.Run(() => PerformIntegrityCheckAsync(_cancellationTokenSource.Token));
            return true;
        }
        finally
        {
            StaticSemaphore.Release();
        }
    }

    public async Task<bool> CancelIntegrityCheckAsync()
    {
        await StaticSemaphore.WaitAsync();
        try
        {
            // Check if there's an active task
            if (_runningTask is { IsCompleted: false })
            {
                Log.Information("Cancelling active integrity check");
                _cancellationTokenSource.Cancel();

                // Wait a short time for graceful cancellation
                try
                {
                    await _runningTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation succeeds
                }

                // Create a new cancellation token source for future operations
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = new CancellationTokenSource();

                return true;
            }

            return false; // No active task to cancel
        }
        finally
        {
            StaticSemaphore.Release();
        }
    }

    public async Task CheckSingleFileIntegrityAsync(DavItem davItem, CancellationToken ct)
    {
        Log.Information("Starting single file integrity check for: {FilePath} (ID: {DavItemId})", davItem.Path, davItem.Id);

        try
        {
            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            var runId = Guid.NewGuid().ToString();
            var (isCorrupt, errorMessage) = await CheckFileIntegrityAsync(davItem, ct);

            string? actionTaken = null;
            if (isCorrupt)
            {
                Log.Warning("Single file integrity check FAILED for {FilePath}: {ErrorMessage}", davItem.Path, errorMessage);
                actionTaken = await HandleCorruptFileAsync(dbClient, davItem, davItem.Path, ct);
            }
            else
            {
                Log.Information("Single file integrity check PASSED for {FilePath}", davItem.Path);
            }

            // Store the check results
            await StoreIntegrityCheckDetailsAsync(dbClient, davItem, !isCorrupt, errorMessage, actionTaken, runId, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during single file integrity check for {FilePath}", davItem.Path);
        }
    }

    public async Task CheckSingleLibraryFileIntegrityAsync(string filePath, CancellationToken ct)
    {
        Log.Information("Starting single library file integrity check for: {FilePath}", filePath);

        try
        {
            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            // Resolve symlink to get the target path (should be in .ids directory)
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Log.Warning("File is not a symlink, unable to check integrity: {FilePath}", filePath);
                return;
            }

            var targetPath = fileInfo.ResolveLinkTarget(true)?.FullName;
            if (string.IsNullOrEmpty(targetPath))
            {
                Log.Warning("Could not resolve symlink target for: {FilePath}", filePath);
                return;
            }

            // Extract the GUID from the target path (should be the filename in .ids directory)
            var targetFileName = Path.GetFileName(targetPath);
            if (!Guid.TryParse(targetFileName, out var davItemId))
            {
                Log.Warning("Target path does not contain valid GUID: {TargetPath}", targetPath);
                return;
            }

            // Look up the DavItem by ID
            var davItem = await dbClient.Ctx.Items
                .Where(item => item.Id == davItemId)
                .FirstOrDefaultAsync(ct);

            if (davItem == null)
            {
                Log.Warning("Could not find DavItem for library file, unable to check integrity: {FilePath} (DavItem ID: {DavItemId})", filePath, davItemId);
                return;
            }

            // Check if it's a streamable file type (NZB or RAR)
            if (davItem.Type != DavItem.ItemType.NzbFile && davItem.Type != DavItem.ItemType.RarFile)
            {
                Log.Warning("DavItem is {ItemType}, only NZB and RAR files supported for streaming integrity check: {FilePath}", davItem.Type, filePath);
                return;
            }

            var runId = Guid.NewGuid().ToString();
            var (isCorrupt, errorMessage) = await CheckFileIntegrityAsync(davItem, ct);

            string? actionTaken = null;
            if (isCorrupt)
            {
                Log.Warning("Single library file integrity check FAILED for {FilePath}: {ErrorMessage}", filePath, errorMessage);
                actionTaken = await HandleCorruptFileAsync(dbClient, davItem, filePath, ct);
            }
            else
            {
                Log.Information("Single library file integrity check PASSED for {FilePath}", filePath);
            }

            // Store the check results (use library file path for storage)
            await StoreIntegrityCheckDetailsAsync(dbClient, filePath, !isCorrupt, errorMessage, actionTaken, runId, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during single library file integrity check for {FilePath}", filePath);
        }
    }

    private async Task StartBackgroundIntegrityCheckAsync(CancellationToken ct)
    {
        Log.Information("Starting background integrity check scheduler");
        var loopCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                loopCount++;
                Log.Information("Background scheduler loop #{LoopCount} - checking if integrity check is needed", loopCount);

                // Check if a check is needed based on last check time
                var shouldRunCheck = await ShouldRunBackgroundCheckAsync(ct);

                if (shouldRunCheck)
                {
                    Log.Information("Background integrity check is due, attempting to start check");

                    await StaticSemaphore.WaitAsync(ct);
                    try
                    {
                        if (_runningTask is { IsCompleted: false })
                        {
                            Log.Information("Integrity check already running, skipping background check");
                            continue; // Skip if already running
                        }

                        Log.Information("Starting background integrity check execution");
                        _runningTask = Task.Run(() => PerformIntegrityCheckAsync(ct), ct);
                        await _runningTask;
                        Log.Information("Background integrity check execution completed");
                    }
                    finally
                    {
                        StaticSemaphore.Release();
                    }
                }
                else
                {
                    Log.Information("Background integrity check not due yet, waiting 10 minutes");
                }

                // Wait 10 minutes before checking again
                Log.Information("Background scheduler sleeping for 10 minutes until next check");
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
            }
            catch (OperationCanceledException)
            {
                Log.Information("Background integrity check scheduler cancelled");
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in background integrity check scheduler, will retry in 30 minutes");
                // Wait before retrying on error
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
            }
        }

        Log.Information("Background integrity check scheduler has exited after {LoopCount} loops", loopCount);
    }

    private async Task PerformIntegrityCheckAsync(CancellationToken ct)
    {
        // Generate a unique run ID for this execution
        var runId = Guid.NewGuid().ToString();
        var startTime = DateTime.UtcNow;

        try
        {
            Log.Information("Starting media integrity check with run ID: {RunId}", runId);
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"starting:{runId}");

            // Store run start time
            await using var startDbContext = new DavDatabaseContext();
            var startDbClient = new DavDatabaseClient(startDbContext);
            await StoreRunTimestampAsync(startDbClient, runId, "start", startTime, CancellationToken.None);

            // Check if library directory is configured (required for arr integration)
            var libraryDir = _configManager.GetLibraryDir();
            if (string.IsNullOrEmpty(libraryDir) && _configManager.GetCorruptFileAction() == "delete_via_arr")
            {
                var errorMsg = "Library directory must be configured for Radarr/Sonarr integration";
                Log.Error(errorMsg);
                _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"failed: {errorMsg}:{runId}");
                return;
            }

            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            // Get items to check based on configuration
            List<IntegrityCheckItem> checkItems;
            if (!string.IsNullOrEmpty(libraryDir) && Directory.Exists(libraryDir))
            {
                // Use library directory for arr integration - resolve symlinks to DavItems
                checkItems = await GetLibraryIntegrityCheckItemsAsync(dbClient, libraryDir, ct);
                Log.Information("Checking {Count} library files in: {LibraryDir}", checkItems.Count, libraryDir);
            }
            else
            {
                // Fallback to internal files for non-arr usage
                var davItems = await GetDavItemsToCheckAsync(dbClient, ct);
                checkItems = davItems.Select(item => new IntegrityCheckItem
                {
                    DavItem = item,
                    LibraryFilePath = null // No library file path for internal items
                }).ToList();
                Log.Information("Checking {Count} internal nzbdav files", checkItems.Count);
            }

            if (checkItems.Count == 0)
            {
                Log.Information("No media files found to check");
                _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"complete: 0/0:{runId}");
                return;
            }

            var totalFiles = checkItems.Count;
            var processedFiles = 0;
            var corruptFiles = 0;

            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));

            foreach (var checkItem in checkItems)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var (isCorrupt, errorMessage) = await CheckFileIntegrityAsync(checkItem.DavItem, ct);

                    if (isCorrupt)
                    {
                        corruptFiles++;
                        Log.Warning("Corrupt media file detected: {FilePath} - {Error}", checkItem.DavItem.Path, errorMessage);

                        // Use library file path for arr integration, or symlink path for internal files
                        var filePath = checkItem.LibraryFilePath ??
                            DatabaseStoreSymlinkFile.GetTargetPath(checkItem.DavItem, _configManager.GetRcloneMountDir());
                        var actionTaken = await HandleCorruptFileAsync(dbClient, checkItem.DavItem, filePath, ct);

                        // Store error details and action taken
                        if (checkItem.LibraryFilePath != null)
                        {
                            await StoreIntegrityCheckDetailsAsync(dbClient, checkItem.LibraryFilePath, false, errorMessage, actionTaken, runId, ct);
                        }
                        else
                        {
                            await StoreIntegrityCheckDetailsAsync(dbClient, checkItem.DavItem, false, errorMessage, actionTaken, runId, ct);
                        }
                    }
                    else
                    {
                        // Store successful check details
                        if (checkItem.LibraryFilePath != null)
                        {
                            await StoreIntegrityCheckDetailsAsync(dbClient, checkItem.LibraryFilePath, true, null, null, runId, ct);
                        }
                        else
                        {
                            await StoreIntegrityCheckDetailsAsync(dbClient, checkItem.DavItem, true, null, null, runId, ct);
                        }
                    }


                    // Note: Integrity check details are now stored in the StoreIntegrityCheckDetailsAsync calls above
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error checking integrity of file: {FilePath}", checkItem.DavItem.Path);
                }

                processedFiles++;
                var progress = $"{processedFiles}/{totalFiles} ({corruptFiles} corrupt)";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"{progress}:{runId}"));
            }

            var finalReport = $"complete: {processedFiles}/{totalFiles} checked, {corruptFiles} corrupt files found";
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"{finalReport}:{runId}");
            Log.Information("Media integrity check completed: {Report}", finalReport);

            // Store run end time
            await using var endDbContext = new DavDatabaseContext();
            var endDbClient = new DavDatabaseClient(endDbContext);
            await StoreRunTimestampAsync(endDbClient, runId, "end", DateTime.UtcNow, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Media integrity check was cancelled");
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"cancelled:{runId}");

            // Store run end time for cancelled
            try
            {
                await using var cancelDbContext = new DavDatabaseContext();
                var cancelDbClient = new DavDatabaseClient(cancelDbContext);
                await StoreRunTimestampAsync(cancelDbClient, runId, "end", DateTime.UtcNow, CancellationToken.None);
            }
            catch (Exception storeEx)
            {
                Log.Warning(storeEx, "Failed to store end time for cancelled run {RunId}", runId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during media integrity check");
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"failed: {ex.Message}:{runId}");

            // Store run end time for failed
            try
            {
                await using var failDbContext = new DavDatabaseContext();
                var failDbClient = new DavDatabaseClient(failDbContext);
                await StoreRunTimestampAsync(failDbClient, runId, "end", DateTime.UtcNow, CancellationToken.None);
            }
            catch (Exception storeEx)
            {
                Log.Warning(storeEx, "Failed to store end time for failed run {RunId}", runId);
            }
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

    private async Task<List<IntegrityCheckItem>> GetLibraryIntegrityCheckItemsAsync(DavDatabaseClient dbClient, string libraryDir, CancellationToken ct)
    {
        var checkItems = new List<IntegrityCheckItem>();
        var cutoffTime = DateTime.Now.AddDays(-_configManager.GetIntegrityCheckIntervalDays());

        try
        {
            // Get media files in library directory recursively (use enumerable for efficiency)
            var allFiles = Directory.EnumerateFiles(libraryDir, "*", SearchOption.AllDirectories)
                .Where(file => IsMediaFile(Path.GetExtension(file).ToLowerInvariant()));

            Log.Information("Scanning library directory for media files...");
            var totalProcessed = 0;
            var maxFiles = _configManager.GetMaxFilesToCheckPerRun();

            // Resolve symlinks to DavItems and filter out recently checked files
            foreach (var filePath in allFiles)
            {
                ct.ThrowIfCancellationRequested();
                totalProcessed++;

                try
                {
                    // Check if we should skip this file based on last check time
                    var fileHash = GetFilePathHash(filePath);
                    var lastCheckConfig = $"integrity.last_check.library.{fileHash}";

                    var lastCheckValue = await dbClient.Ctx.ConfigItems
                        .Where(c => c.ConfigName == lastCheckConfig)
                        .Select(c => c.ConfigValue)
                        .FirstOrDefaultAsync(ct);

                    if (lastCheckValue != null && DateTime.TryParse(lastCheckValue, out var lastCheck) && lastCheck > cutoffTime)
                    {
                        continue; // Skip recently checked files
                    }

                    // Resolve symlink to get the target path (should be in .ids directory)
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        Log.Debug("Skipping non-symlink file: {FilePath}", filePath);
                        continue; // Not a symlink, skip
                    }

                    var targetPath = fileInfo.ResolveLinkTarget(true)?.FullName;
                    if (string.IsNullOrEmpty(targetPath))
                    {
                        Log.Warning("Could not resolve symlink target for: {FilePath}", filePath);
                        continue;
                    }

                    // Extract the GUID from the target path (should be the filename in .ids directory)
                    var targetFileName = Path.GetFileName(targetPath);
                    if (!Guid.TryParse(targetFileName, out var davItemId))
                    {
                        Log.Debug("Target path does not contain valid GUID: {TargetPath}", targetPath);
                        continue;
                    }

                    // Look up the DavItem by ID - first check if it exists at all
                    var anyDavItem = await dbClient.Ctx.Items
                        .Where(item => item.Id == davItemId)
                        .FirstOrDefaultAsync(ct);

                    if (anyDavItem == null)
                    {
                        // DavItem doesn't exist - likely imported outside nzbdav or deleted
                        Log.Debug("Skipping {FilePath}: DavItem {DavItemId} not found (file may have been imported outside nzbdav)",
                            Path.GetFileName(filePath), davItemId);
                        continue;
                    }

                    // Check if it's a streamable file type (NZB or RAR)
                    if (anyDavItem.Type != DavItem.ItemType.NzbFile && anyDavItem.Type != DavItem.ItemType.RarFile)
                    {
                        Log.Debug("Skipping {FilePath}: DavItem is {ItemType} (only NZB and RAR files supported for streaming integrity check)",
                            Path.GetFileName(filePath), anyDavItem.Type);
                        continue;
                    }

                    // Valid streamable file found
                    checkItems.Add(new IntegrityCheckItem
                    {
                        DavItem = anyDavItem,
                        LibraryFilePath = filePath
                    });
                    Log.Debug("Added {FilePath} for integrity check (DavItem: {DavItemPath}, Type: {ItemType})",
                        Path.GetFileName(filePath), anyDavItem.Path, anyDavItem.Type);

                    // Limit the number of files per run
                    if (checkItems.Count >= maxFiles)
                    {
                        Log.Information("Reached file limit of {MaxFiles}, stopping scan", maxFiles);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error processing library file: {FilePath}", filePath);
                }
            }

            var skippedCount = totalProcessed - checkItems.Count;
            Log.Information("Library scan complete: {ResolvedCount} files ready for integrity check, {SkippedCount} files skipped",
                checkItems.Count, skippedCount);

            if (skippedCount > 0)
            {
                Log.Information("Skipped files are either: files imported outside nzbdav, deleted DavItems, or unsupported file types");
            }

            return checkItems;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error scanning library directory: {LibraryDir}", libraryDir);
            return checkItems;
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
        var configValue = DateTime.UtcNow.ToString("O");

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

        // Store the original file path for display purposes
        var pathConfigName = $"integrity.path.library.{fileHash}";
        var pathConfig = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == pathConfigName, ct);

        if (pathConfig != null)
        {
            pathConfig.ConfigValue = filePath;
        }
        else
        {
            dbClient.Ctx.ConfigItems.Add(new ConfigItem
            {
                ConfigName = pathConfigName,
                ConfigValue = filePath
            });
        }

        await dbClient.Ctx.SaveChangesAsync(ct);
    }

    private async Task<(bool isCorrupt, string? errorMessage)> CheckFileIntegrityAsync(DavItem davItem, CancellationToken ct)
    {
        var fileExtension = Path.GetExtension(davItem.Name).ToLowerInvariant();

        // Check if it's a media file type we can verify
        if (!IsMediaFile(fileExtension))
        {
            return (false, null); // Not a media file, consider it valid
        }

        try
        {
            // Get the file data from database and create appropriate stream
            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            Stream stream;
            if (davItem.Type == DavItem.ItemType.NzbFile)
            {
                var nzbFile = await dbClient.GetNzbFileAsync(davItem.Id, ct);
                if (nzbFile == null)
                {
                    Log.Warning("Could not find NZB file data for {FilePath} (ID: {Id})", davItem.Path, davItem.Id);
                    return (true, "NZB file data not found in database"); // Consider missing NZB data as corrupt
                }
                stream = _usenetClient.GetFileStream(nzbFile.SegmentIds, davItem.FileSize!.Value, _configManager.GetConnectionsPerStream());
            }
            else if (davItem.Type == DavItem.ItemType.RarFile)
            {
                var rarFile = await dbClient.Ctx.RarFiles.Where(x => x.Id == davItem.Id).FirstOrDefaultAsync(ct);
                if (rarFile == null)
                {
                    Log.Warning("Could not find RAR file data for {FilePath} (ID: {Id})", davItem.Path, davItem.Id);
                    return (true, "RAR file data not found in database"); // Consider missing RAR data as corrupt
                }
                stream = new RarFileStream(rarFile.RarParts, _usenetClient, _configManager.GetConnectionsPerStream());
            }
            else
            {
                Log.Debug("Skipping integrity check for unsupported file type: {FilePath} (Type: {ItemType})", davItem.Path, davItem.Type);
                return (false, null); // Consider unsupported types as valid
            }

            // Use FFMpegCore to analyze the entire stream for media integrity
            var enableMp4DeepScan = _configManager.IsMp4DeepScanEnabled();
            var isValid = await FfprobeUtil.IsValidMediaStreamAsync(stream, davItem.Path, enableMp4DeepScan, ct);

            // Clean up the stream
            await stream.DisposeAsync();

            var isCorrupt = !isValid;

            if (isCorrupt)
            {
                Log.Warning("File integrity check FAILED for {FilePath}: Invalid or corrupt media content", davItem.Path);
                return (true, "FFmpeg validation failed - invalid or corrupt media content");
            }
            else
            {
                Log.Information("File integrity check PASSED for {FilePath}", davItem.Path);
                return (false, null);
            }
        }
        catch (UsenetArticleNotFoundException ex)
        {
            Log.Warning("Missing usenet articles detected for {FilePath}: {Message}", davItem.Path, ex.Message);

            // Missing articles mean the file is definitely corrupt/incomplete
            // This is similar to how the download process handles missing articles
            return (true, $"Missing usenet articles: {ex.Message}"); // Mark as corrupt
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error running ffprobe on {FilePath}", davItem.Path);

            // If ffprobe is not found or can't be started, this is a configuration issue
            // We should treat this as a problem that needs attention
            if (ex is System.ComponentModel.Win32Exception win32Ex && win32Ex.NativeErrorCode == 2)
            {
                Log.Error("ffprobe not found - please ensure FFmpeg is installed. File integrity cannot be verified: {FilePath}", davItem.Path);
                // Return true (corrupt) to indicate there's an issue that needs attention
                // This prevents silent failures where users think files are checked but they're not
                return (true, "FFmpeg/ffprobe not found - please ensure FFmpeg is installed");
            }

            // For other errors, assume file is ok but log the issue
            return (false, $"Error during integrity check: {ex.Message}");
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


    private async Task<string> HandleCorruptFileAsync(DavDatabaseClient dbClient, DavItem? davItem, string filePath, CancellationToken ct)
    {
        var action = _configManager.GetCorruptFileAction();

        // Auto-monitor corrupt files before deletion if enabled (for re-download)
        if (_configManager.IsAutoMonitorEnabled() && (action == "delete" || action == "delete_via_arr"))
        {
            try
            {
                Log.Information("Auto-monitoring corrupt file before deletion for re-download: {FilePath}", filePath);
                var monitorSuccess = await _arrManager.MonitorFileInArrAsync(filePath, ct);
                if (monitorSuccess)
                {
                    Log.Information("Successfully monitored corrupt file for re-download before deletion: {FilePath}", filePath);
                }
                else
                {
                    Log.Warning("Failed to monitor corrupt file before deletion: {FilePath}", filePath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error monitoring corrupt file before deletion: {FilePath}", filePath);
            }
        }

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
                    return "File deleted successfully";
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error deleting corrupt file: {FilePath}", filePath);
                    return $"Failed to delete file: {ex.Message}";
                }

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
                        return "File deleted via Radarr/Sonarr successfully";
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
                            return "Failed to delete via Radarr/Sonarr, used direct deletion fallback";
                        }
                        else
                        {
                            Log.Information("Direct deletion fallback is disabled, leaving corrupt file in place: {FilePath}", filePath);
                            Log.Warning("Corrupt file was not deleted by Radarr/Sonarr and direct deletion fallback is disabled. File remains: {FilePath}", filePath);
                            return "Failed to delete via Radarr/Sonarr, direct deletion fallback disabled";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error deleting corrupt file via Radarr/Sonarr: {FilePath}", filePath);
                    return $"Error during Radarr/Sonarr deletion: {ex.Message}";
                }

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
                        return $"File moved to quarantine: {quarantinePath}";
                    }
                    return "File not found for quarantine";
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error quarantining corrupt file: {FilePath}", filePath);
                    return $"Failed to quarantine file: {ex.Message}";
                }

            case "log":
            default:
                // Just log the issue (already done above)
                return "Logged corrupt file (no action taken)";
        }
    }

    private async Task StoreIntegrityCheckDetailsAsync(DavDatabaseClient dbClient, string filePath, bool isValid, string? errorMessage, string? actionTaken, string runId, CancellationToken ct)
    {
        var fileHash = GetFilePathHash(filePath);
        await StoreIntegrityCheckDetailsForHashAsync(dbClient, fileHash, isValid, errorMessage, actionTaken, runId, ct);

        // Also store the file path for display purposes (library files)
        var pathConfigName = $"integrity.path.library.{fileHash}";
        var pathConfig = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == pathConfigName, ct);

        if (pathConfig != null)
        {
            pathConfig.ConfigValue = filePath;
        }
        else
        {
            dbClient.Ctx.ConfigItems.Add(new ConfigItem
            {
                ConfigName = pathConfigName,
                ConfigValue = filePath
            });
        }

        await dbClient.Ctx.SaveChangesAsync(ct);
    }

    private async Task StoreIntegrityCheckDetailsAsync(DavDatabaseClient dbClient, DavItem davItem, bool isValid, string? errorMessage, string? actionTaken, string runId, CancellationToken ct)
    {
        await StoreIntegrityCheckDetailsForHashAsync(dbClient, davItem.Id.ToString(), isValid, errorMessage, actionTaken, runId, ct);
    }

    private async Task StoreIntegrityCheckDetailsForHashAsync(DavDatabaseClient dbClient, string identifier, bool isValid, string? errorMessage, string? actionTaken, string runId, CancellationToken ct)
    {
        var now = DateTime.UtcNow.ToString("O");

        // Determine if this is a library file or internal DavItem
        bool isLibraryFile = !Guid.TryParse(identifier, out _);
        string prefix = isLibraryFile ? "library." : "";

        // Store last check timestamp
        var lastCheckConfigName = $"integrity.last_check.{prefix}{identifier}";
        var lastCheckConfig = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == lastCheckConfigName, ct);
        if (lastCheckConfig != null)
        {
            lastCheckConfig.ConfigValue = now;
        }
        else
        {
            dbClient.Ctx.ConfigItems.Add(new ConfigItem
            {
                ConfigName = lastCheckConfigName,
                ConfigValue = now
            });
        }

        // Store status
        var statusConfigName = $"integrity.status.{prefix}{identifier}";
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

        // Store error message if present
        if (!string.IsNullOrEmpty(errorMessage))
        {
            var errorConfigName = $"integrity.error.{prefix}{identifier}";
            var errorConfig = await dbClient.Ctx.ConfigItems
                .FirstOrDefaultAsync(c => c.ConfigName == errorConfigName, ct);
            if (errorConfig != null)
            {
                errorConfig.ConfigValue = errorMessage;
            }
            else
            {
                dbClient.Ctx.ConfigItems.Add(new ConfigItem
                {
                    ConfigName = errorConfigName,
                    ConfigValue = errorMessage
                });
            }
        }

        // Store action taken if present
        if (!string.IsNullOrEmpty(actionTaken))
        {
            var actionConfigName = $"integrity.action.{prefix}{identifier}";
            var actionConfig = await dbClient.Ctx.ConfigItems
                .FirstOrDefaultAsync(c => c.ConfigName == actionConfigName, ct);
            if (actionConfig != null)
            {
                actionConfig.ConfigValue = actionTaken;
            }
            else
            {
                dbClient.Ctx.ConfigItems.Add(new ConfigItem
                {
                    ConfigName = actionConfigName,
                    ConfigValue = actionTaken
                });
            }
        }

        // Store run ID for grouping by execution
        var runIdConfigName = $"integrity.run_id.{prefix}{identifier}";
        var runIdConfig = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == runIdConfigName, ct);
        if (runIdConfig != null)
        {
            runIdConfig.ConfigValue = runId;
        }
        else
        {
            dbClient.Ctx.ConfigItems.Add(new ConfigItem
            {
                ConfigName = runIdConfigName,
                ConfigValue = runId
            });
        }

        await dbClient.Ctx.SaveChangesAsync(ct);
    }

    private async Task UpdateLastIntegrityCheckAsync(DavDatabaseClient dbClient, DavItem davItem, bool isValid, CancellationToken ct)
    {
        var configName = $"integrity.last_check.{davItem.Id}";
        var configValue = DateTime.UtcNow.ToString("O");

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

    private async Task StoreRunTimestampAsync(DavDatabaseClient dbClient, string runId, string type, DateTime timestamp, CancellationToken ct)
    {
        var configName = $"integrity.run_{type}.{runId}";
        var configValue = timestamp.ToString("O");

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

        await dbClient.Ctx.SaveChangesAsync(ct);
    }

    private async Task<bool> ShouldRunBackgroundCheckAsync(CancellationToken ct)
    {
        try
        {
            var intervalHours = _configManager.GetIntegrityCheckIntervalHours();
            var intervalDays = _configManager.GetIntegrityCheckIntervalDays();

            Log.Information("Checking if background integrity check should run - intervalHours: {IntervalHours}, intervalDays: {IntervalDays}", intervalHours, intervalDays);

            // Use the less restrictive (shorter) of the two intervals for more frequent checking
            var effectiveIntervalHours = Math.Min(intervalHours, intervalDays * 24);

            Log.Information("Effective interval calculated as: {EffectiveHours} hours", effectiveIntervalHours);

            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            // Get the most recent check time from any file
            var mostRecentConfig = await dbClient.Ctx.ConfigItems
                .Where(c => c.ConfigName.StartsWith("integrity.last_check."))
                .OrderByDescending(c => c.ConfigValue)
                .FirstOrDefaultAsync(ct);

            if (mostRecentConfig == null)
            {
                Log.Information("No previous integrity checks found, background check is due");
                return true; // No previous checks, so run now
            }

            Log.Information("Found most recent check config: {ConfigName} = {ConfigValue}", mostRecentConfig.ConfigName, mostRecentConfig.ConfigValue);

            if (!DateTime.TryParse(mostRecentConfig.ConfigValue, out var lastCheckTime))
            {
                Log.Warning("Could not parse last check time: {ConfigValue}, assuming check is due", mostRecentConfig.ConfigValue);
                return true; // Invalid date, assume we should check
            }

            var currentTime = DateTime.UtcNow;
            var lastCheckTimeUtc = lastCheckTime.ToUniversalTime();
            var timeSinceLastCheck = currentTime - lastCheckTimeUtc;
            var hoursUntilNext = effectiveIntervalHours - timeSinceLastCheck.TotalHours;

            Log.Information("Background check evaluation: Current time: {CurrentTime}, Last check: {LastCheck} (UTC: {LastCheckUtc}), hours since: {HoursSince:F1}, interval: {Interval}h, hours until next: {UntilNext:F1}",
                currentTime, lastCheckTime, lastCheckTimeUtc, timeSinceLastCheck.TotalHours, effectiveIntervalHours, hoursUntilNext);

            var shouldRun = hoursUntilNext <= 0;
            Log.Information("Should run background check: {ShouldRun}", shouldRun);

            return shouldRun; // Check is due if we've passed the interval
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error determining if background check should run, returning false");
            return false; // Don't run on error
        }
    }

    public void Dispose()
    {
        Log.Information("Disposing MediaIntegrityService - cancelling background scheduler");
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _semaphore?.Dispose();
    }
}
