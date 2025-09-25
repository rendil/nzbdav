using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.IntegrityResults;
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

public class IntegrityRunStatus
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; set; }

    [JsonPropertyName("status")]
    public IntegrityCheckRun.StatusOption Status { get; set; }

    [JsonPropertyName("startTime")]
    public string? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public string? EndTime { get; set; }

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("validFiles")]
    public int ValidFiles { get; set; }

    [JsonPropertyName("corruptFiles")]
    public int CorruptFiles { get; set; }

    [JsonPropertyName("processedFiles")]
    public int ProcessedFiles { get; set; }

    [JsonPropertyName("currentFile")]
    public string? CurrentFile { get; set; }

    [JsonPropertyName("progressPercentage")]
    public double? ProgressPercentage { get; set; }

    [JsonPropertyName("parameters")]
    public IntegrityCheckRunParameters? Parameters { get; set; }

    [JsonPropertyName("files")]
    public List<IntegrityFileResult> Files { get; set; } = new();
}

public class IntegrityCheckRunParameters
{
    [JsonPropertyName("scanDirectory")]
    public string? ScanDirectory { get; set; }

    [JsonPropertyName("maxFilesToCheck")]
    public int MaxFilesToCheck { get; set; }

    [JsonPropertyName("corruptFileAction")]
    public IntegrityCheckRun.CorruptFileActionOption CorruptFileAction { get; set; } = IntegrityCheckRun.CorruptFileActionOption.Log;

    [JsonPropertyName("mp4DeepScan")]
    public bool Mp4DeepScan { get; set; }

    [JsonPropertyName("autoMonitor")]
    public bool AutoMonitor { get; set; }

    [JsonPropertyName("unmonitorValidatedFiles")]
    public bool UnmonitorValidatedFiles { get; set; }

    [JsonPropertyName("directDeletionFallback")]
    public bool DirectDeletionFallback { get; set; }

    [JsonPropertyName("runType")]
    public IntegrityCheckRun.RunTypeOption RunType { get; set; } = IntegrityCheckRun.RunTypeOption.Manual;
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
        _configManager = configManager;
        _websocketManager = websocketManager;
        _arrManager = arrManager;
        _usenetClient = usenetClient;
        _cancellationTokenSource = CancellationTokenSource
            .CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
        _ = StartBackgroundIntegrityCheckAsync(_cancellationTokenSource.Token);
    }

    public IntegrityCheckRunParameters GetDefaultRunParameters()
    {
        return new IntegrityCheckRunParameters
        {
            ScanDirectory = _configManager.GetLibraryDir(),
            MaxFilesToCheck = _configManager.GetMaxFilesToCheckPerRun(),
            CorruptFileAction = Enum.TryParse<IntegrityCheckRun.CorruptFileActionOption>(_configManager.GetCorruptFileAction(), true, out var action) ? action : IntegrityCheckRun.CorruptFileActionOption.Log,
            Mp4DeepScan = _configManager.IsMp4DeepScanEnabled(),
            AutoMonitor = _configManager.IsAutoMonitorEnabled(),
            UnmonitorValidatedFiles = _configManager.IsUnmonitorValidatedFilesEnabled(),
            DirectDeletionFallback = _configManager.IsDirectDeletionFallbackEnabled(),
            RunType = IntegrityCheckRun.RunTypeOption.Manual
        };
    }

    public async Task<bool> TriggerManualIntegrityCheckWithRunIdAsync(IntegrityCheckRunParameters? parameters, string runId)
    {
        await StaticSemaphore.WaitAsync();
        try
        {
            // if the task is already running, return immediately.
            if (_runningTask is { IsCompleted: false })
            {
                Log.Information("Manual integrity check skipped: task already running (runId: {RunId})", runId);
                _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"failed: Already running:{runId}");
                return false;
            }

            // Use provided parameters or get defaults
            var runParams = parameters ?? GetDefaultRunParameters();

            // Get the list of files to check
            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            // Send scanning message to inform frontend we're discovering files
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"scanning:{runId}");

            var checkItems = await GetIntegrityCheckItemsAsync(dbClient, runParams, _cancellationTokenSource.Token);

            // Check if there are no files to process
            if (checkItems.Count == 0)
            {
                Log.Information("Manual integrity check skipped: no files eligible for checking (runId: {RunId})", runId);
                _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"no_files:{runId}");

                // Send completion message for consistency with successful runs
                _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"complete: 0/0:{runId}");

                return false; // Return false to indicate the run didn't start
            }

            // otherwise, start the task and return immediately (don't wait for completion).
            _runningTask = Task.Run(() => PerformIntegrityCheckAsync(_cancellationTokenSource.Token, runId, checkItems, runParams));
            Log.Information("Manual integrity check queued successfully (runId: {RunId})", runId);
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

    public async Task<IntegrityRunStatus?> GetRunStatusAsync(string runId)
    {
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);

        // Get run from the new table
        var run = await dbClient.Ctx.IntegrityCheckRuns
            .FirstOrDefaultAsync(r => r.RunId == runId);

        if (run == null)
        {
            return null; // Run not found
        }

        // Check if this is the currently running task
        var isCurrentlyRunning = _runningTask is { IsCompleted: false } && run.IsRunning;

        // Convert to parameters object for compatibility
        var parameters = new IntegrityCheckRunParameters
        {
            ScanDirectory = run.ScanDirectory,
            MaxFilesToCheck = run.MaxFilesToCheck,
            CorruptFileAction = run.CorruptFileAction,
            Mp4DeepScan = run.Mp4DeepScan,
            AutoMonitor = run.AutoMonitor,
            DirectDeletionFallback = run.DirectDeletionFallback,
            RunType = run.RunType
        };

        // Get files for this specific run
        var runFiles = await dbClient.Ctx.IntegrityCheckFileResults
            .Where(f => f.RunId == runId)
            .OrderByDescending(f => f.LastChecked)
            .ToListAsync();

        // Convert to the expected IntegrityFileResult format
        var fileResults = runFiles.Select(f => new IntegrityFileResult
        {
            FileId = f.FileId,
            FilePath = f.FilePath,
            FileName = f.FileName,
            IsLibraryFile = f.IsLibraryFile,
            LastChecked = f.LastChecked.ToUniversalTime().ToString("O"),
            Status = f.Status,
            ErrorMessage = f.ErrorMessage,
            ActionTaken = f.ActionTaken,
            RunId = f.RunId
        }).ToList();

        // Calculate counters from actual stored file results for accuracy
        var actualValidFiles = fileResults.Count(f => f.Status == IntegrityCheckFileResult.StatusOption.Valid);
        var actualCorruptFiles = fileResults.Count(f => f.Status == IntegrityCheckFileResult.StatusOption.Corrupt);
        var actualTotalFiles = fileResults.Count;

        var result = new IntegrityRunStatus
        {
            RunId = run.RunId,
            IsRunning = isCurrentlyRunning,
            Status = run.Status, // JsonStringEnumConverter handles serialization
            StartTime = run.StartTime.ToUniversalTime().ToString("O"),
            EndTime = run.EndTime?.ToUniversalTime().ToString("O"),
            TotalFiles = Math.Max(run.TotalFiles, actualTotalFiles), // Use higher value for more accuracy
            ValidFiles = actualValidFiles, // Use actual count from stored results
            CorruptFiles = actualCorruptFiles, // Use actual count from stored results
            ProcessedFiles = actualTotalFiles, // Actual processed files
            CurrentFile = run.CurrentFile,
            ProgressPercentage = run.ProgressPercentage,
            Parameters = parameters,
            Files = fileResults
        };


        return result;
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

            IntegrityCheckFileResult.ActionOption? actionTaken = null;
            if (isCorrupt)
            {
                Log.Warning("Single file integrity check FAILED for {FilePath}: {ErrorMessage}", davItem.Path, errorMessage);
                actionTaken = await HandleCorruptFileAsync(dbClient, davItem, davItem.Path, ct);
            }
            else
            {
                Log.Information("Single file integrity check PASSED for {FilePath}", davItem.Path);
            }

            // Check results are now stored via StoreFileResultAsync in the main loop
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
            var (isCorrupt, errorMessage) = await CheckFileIntegrityAsync(davItem, ct, filePath);

            IntegrityCheckFileResult.ActionOption? actionTaken = null;
            if (isCorrupt)
            {
                Log.Warning("Single library file integrity check FAILED for {FilePath}: {ErrorMessage}", filePath, errorMessage);
                actionTaken = await HandleCorruptFileAsync(dbClient, davItem, filePath, ct);
            }
            else
            {
                Log.Information("Single library file integrity check PASSED for {FilePath}", filePath);
            }

            // Check results are now stored via StoreFileResultAsync in the main loop
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during single library file integrity check for {FilePath}", filePath);
        }
    }

    private async Task StartBackgroundIntegrityCheckAsync(CancellationToken ct)
    {
        // Wait 2 minutes for backend to start before starting the scheduler
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Start background integrity checking if both integrity checks and scheduled checks are enabled
                if (!_configManager.IsIntegrityCheckingEnabled() || !_configManager.IsScheduledIntegrityCheckingEnabled())
                {
                    // Check every 2 minutes if a user enables scheduled checks. if not, continue to wait.
                    await Task.Delay(TimeSpan.FromMinutes(2), ct);
                    continue;
                }

                // Check if a check is needed based on last check time and get next check time
                var (shouldRunCheck, nextCheckInMinutes) = await ShouldRunBackgroundCheckWithTimingAsync(ct);

                if (shouldRunCheck)
                {
                    Log.Information("Background integrity check is due, checking for eligible files");

                    await StaticSemaphore.WaitAsync(ct);
                    try
                    {
                        if (_runningTask is { IsCompleted: false })
                        {
                            Log.Debug("Integrity check already running, skipping background check");
                            continue; // Skip if already running
                        }

                        // Get default parameters for scheduled run
                        var scheduledParams = GetDefaultRunParameters();
                        scheduledParams.RunType = IntegrityCheckRun.RunTypeOption.Scheduled;

                        // Check if there are any files eligible for checking before creating database record
                        await using var countDbContext = new DavDatabaseContext();
                        var countDbClient = new DavDatabaseClient(countDbContext);

                        // Get the actual list of files to check to determine if we should proceed
                        var checkItems = await GetIntegrityCheckItemsAsync(countDbClient, scheduledParams, ct);

                        if (checkItems.Count == 0)
                        {
                            Log.Information("Background integrity check skipped: no files eligible for checking");
                            // Don't use continue here - we still need to wait before checking again
                        }
                        else
                        {
                            Log.Information("Background integrity check starting: {EligibleFiles} files eligible for checking", checkItems.Count);

                            // Generate a unique run ID for the scheduled check
                            var scheduledRunId = Guid.NewGuid().ToString();
                            var startTime = DateTime.UtcNow;

                            // Create the database record FIRST with "initialized" status
                            await using var dbContext = new DavDatabaseContext();
                            var dbClient = new DavDatabaseClient(dbContext);

                            // Clean up old integrity check runs before starting a new one
                            await CleanupOldIntegrityRunsAsync(dbClient, ct);

                            var integrityRun = new IntegrityCheckRun
                            {
                                RunId = scheduledRunId,
                                StartTime = startTime,
                                RunType = scheduledParams.RunType,
                                ScanDirectory = scheduledParams.ScanDirectory,
                                MaxFilesToCheck = scheduledParams.MaxFilesToCheck,
                                CorruptFileAction = scheduledParams.CorruptFileAction,
                                Mp4DeepScan = scheduledParams.Mp4DeepScan,
                                AutoMonitor = scheduledParams.AutoMonitor,
                                DirectDeletionFallback = scheduledParams.DirectDeletionFallback,
                                ValidFiles = 0,
                                CorruptFiles = 0,
                                TotalFiles = 0,
                                IsRunning = false, // Will be set to true when task actually starts
                                Status = IntegrityCheckRun.StatusOption.Initialized
                            };

                            dbClient.Ctx.IntegrityCheckRuns.Add(integrityRun);
                            await dbClient.Ctx.SaveChangesAsync(ct);

                            _runningTask = Task.Run(() => PerformIntegrityCheckAsync(ct, scheduledRunId, checkItems, scheduledParams), ct);
                            await _runningTask;
                        }
                    }
                    finally
                    {
                        StaticSemaphore.Release();
                    }
                }
                else
                {
                    Log.Debug("Background integrity check not due yet, next check in {NextCheckMinutes} minutes", nextCheckInMinutes);
                }

                // Wait until next check is due
                var waitMinutes = nextCheckInMinutes;
                if (waitMinutes <= 0) waitMinutes = 10; // Fallback to 10 minutes if calculation fails

                Log.Debug("Waiting {WaitMinutes} minutes before next scheduler check", waitMinutes);
                await Task.Delay(TimeSpan.FromMinutes(waitMinutes), ct);
            }
            catch (OperationCanceledException)
            {
                Log.Information("Background integrity check scheduler cancelled");
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in background integrity check scheduler");
                // Wait before retrying on error
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
            }
        }
    }

    private async Task<List<IntegrityCheckItem>> GetIntegrityCheckItemsAsync(DavDatabaseClient dbClient, IntegrityCheckRunParameters runParams, CancellationToken ct)
    {
        var scanDirectory = runParams.ScanDirectory ?? _configManager.GetLibraryDir();

        List<IntegrityCheckItem> checkItems;
        if (!string.IsNullOrEmpty(scanDirectory) && Directory.Exists(scanDirectory))
        {
            // Use specified directory for scanning - resolve symlinks to DavItems
            checkItems = await GetLibraryIntegrityCheckItemsAsync(dbClient, scanDirectory, runParams.MaxFilesToCheck, ct);
        }
        else
        {
            // Fallback to internal files for non-library usage
            var davItems = await GetDavItemsToCheckAsync(dbClient, runParams.MaxFilesToCheck, ct);
            checkItems = davItems.Select(item => new IntegrityCheckItem
            {
                DavItem = item,
                LibraryFilePath = null // No library file path for internal items
            }).ToList();
        }

        // Apply max files limit from parameters for internal files only
        // (Library files are already limited during scan)
        if (string.IsNullOrEmpty(scanDirectory) && checkItems.Count > runParams.MaxFilesToCheck)
        {
            checkItems = checkItems.Take(runParams.MaxFilesToCheck).ToList();
        }

        return checkItems;
    }

    private async Task PerformIntegrityCheckAsync(CancellationToken ct, string runId, List<IntegrityCheckItem> checkItems, IntegrityCheckRunParameters? parameters = null)
    {
        var startTime = DateTime.UtcNow;

        // Use provided parameters or get defaults
        var runParams = parameters ?? GetDefaultRunParameters();

        // Declare counters outside try block so they're available in catch blocks
        var processedFiles = 0;
        var corruptFiles = 0;

        try
        {
            Log.Information("Starting media integrity check with run ID: {RunId}, type: {RunType}", runId, runParams.RunType);
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"starting:{runId}");

            // Update the existing run record to "started" status
            await using var startDbContext = new DavDatabaseContext();
            var startDbClient = new DavDatabaseClient(startDbContext);

            var integrityRun = await startDbClient.Ctx.IntegrityCheckRuns
                .FirstOrDefaultAsync(r => r.RunId == runId, CancellationToken.None);

            if (integrityRun == null)
            {
                Log.Error("Integrity run record not found for ID: {RunId}", runId);
                return;
            }

            // Update to "started" status
            integrityRun.IsRunning = true;
            integrityRun.Status = IntegrityCheckRun.StatusOption.Started;
            await startDbClient.Ctx.SaveChangesAsync(CancellationToken.None);

            // Check if library directory is configured (required for arr integration)
            var scanDirectory = runParams.ScanDirectory ?? _configManager.GetLibraryDir();
            if (string.IsNullOrEmpty(scanDirectory) && runParams.CorruptFileAction == IntegrityCheckRun.CorruptFileActionOption.DeleteViaArr)
            {
                var errorMsg = "Scan directory must be configured for Radarr/Sonarr integration";
                Log.Error(errorMsg);
                _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"failed: {errorMsg}:{runId}");

                // Update run status to failed
                if (integrityRun != null)
                {
                    integrityRun.Status = IntegrityCheckRun.StatusOption.Failed;
                    integrityRun.IsRunning = false;
                    integrityRun.EndTime = DateTime.UtcNow;
                    await startDbClient.Ctx.SaveChangesAsync(CancellationToken.None);
                }
                return;
            }

            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            // Log the file count and type
            if (!string.IsNullOrEmpty(scanDirectory) && Directory.Exists(scanDirectory))
            {
                Log.Information("Checking {Count} library files in: {ScanDirectory}", checkItems.Count, scanDirectory);
            }
            else
            {
                Log.Information("Checking {Count} internal nzbdav files", checkItems.Count);
            }

            if (checkItems.Count == 0)
            {
                Log.Information("No media files found to check");
                _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"complete: 0/0:{runId}");
                return;
            }

            var totalFiles = checkItems.Count;

            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));

            foreach (var checkItem in checkItems)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var (isCorrupt, errorMessage) = await CheckFileIntegrityAsync(checkItem.DavItem, ct, checkItem.LibraryFilePath);

                    if (isCorrupt)
                    {
                        corruptFiles++;
                        Log.Warning("Corrupt media file detected: {FilePath} - {Error}", checkItem.DavItem.Path, errorMessage);

                        // Use library file path for arr integration, or symlink path for internal files
                        var filePath = checkItem.LibraryFilePath ??
                            DatabaseStoreSymlinkFile.GetTargetPath(checkItem.DavItem, _configManager.GetRcloneMountDir());
                        var actionTaken = await HandleCorruptFileAsync(dbClient, checkItem.DavItem, filePath, runParams.CorruptFileAction, ct);

                        // Store error details and action taken
                        if (checkItem.LibraryFilePath != null)
                        {
                            await StoreFileResultAsync(dbClient, checkItem.LibraryFilePath, checkItem.DavItem.Id.ToString(), true, false, errorMessage, actionTaken, runId, ct);
                        }
                        else
                        {
                            await StoreFileResultAsync(dbClient, checkItem.DavItem.Path, checkItem.DavItem.Id.ToString(), false, false, errorMessage, actionTaken, runId, ct);
                        }
                    }
                    else
                    {
                        // For runs with unmonitor option enabled, unmonitor successfully validated files
                        if (runParams.UnmonitorValidatedFiles && checkItem.LibraryFilePath != null)
                        {
                            try
                            {
                                var unmonitorSuccess = await _arrManager.UnmonitorFileInArrAsync(checkItem.LibraryFilePath, ct);
                                if (unmonitorSuccess)
                                {
                                    Log.Debug("Successfully unmonitored validated file: {FilePath}", checkItem.LibraryFilePath);
                                }
                                else
                                {
                                    Log.Warning("Failed to unmonitor validated file: {FilePath}", checkItem.LibraryFilePath);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Error unmonitoring validated file: {FilePath}", checkItem.LibraryFilePath);
                            }
                        }

                        // Store successful check details
                        if (checkItem.LibraryFilePath != null)
                        {
                            await StoreFileResultAsync(dbClient, checkItem.LibraryFilePath, checkItem.DavItem.Id.ToString(), true, true, null, null, runId, ct);
                        }
                        else
                        {
                            await StoreFileResultAsync(dbClient, checkItem.DavItem.Path, checkItem.DavItem.Id.ToString(), false, true, null, null, runId, ct);
                        }
                    }


                    // Note: Integrity check details are now stored in the StoreFileResultAsync calls above
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during file processing - propagate to main handler
                    // This includes TaskCanceledException which derives from OperationCanceledException
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error checking integrity of file: {FilePath}", checkItem.DavItem.Path);
                }

                processedFiles++;
                var progress = $"{processedFiles}/{totalFiles} ({corruptFiles} corrupt)";
                var progressPercentage = totalFiles > 0 ? (double)processedFiles / totalFiles * 100 : 0;

                // Update run progress in database
                var validFiles = processedFiles - corruptFiles;
                // Pass totalFiles on first update to set it in the database
                var totalFilesToPass = processedFiles == 1 ? totalFiles : (int?)null;
                await UpdateRunProgressAsync(runId, validFiles, corruptFiles, checkItem.DavItem.Path, progressPercentage, ct, totalFilesToPass);

                debounce(() => _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"{progress}:{runId}"));
            }

            var finalReport = $"complete: {processedFiles}/{totalFiles} checked, {corruptFiles} corrupt files found";
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"{finalReport}:{runId}");
            Log.Information("Media integrity check completed: {Report}", finalReport);

            // Update run record with completion
            await using var endDbContext = new DavDatabaseContext();
            var endDbClient = new DavDatabaseClient(endDbContext);

            var completedRun = await endDbClient.Ctx.IntegrityCheckRuns
                .FirstOrDefaultAsync(r => r.RunId == runId);
            if (completedRun != null)
            {
                var finalValidFiles = processedFiles - corruptFiles;

                completedRun.EndTime = DateTime.UtcNow;
                completedRun.IsRunning = false;
                completedRun.Status = IntegrityCheckRun.StatusOption.Completed;
                completedRun.TotalFiles = processedFiles;
                completedRun.ValidFiles = finalValidFiles;
                completedRun.CorruptFiles = corruptFiles;
                completedRun.CurrentFile = null;
                completedRun.ProgressPercentage = null;

                Log.Information("Completing run {RunId}: TotalFiles={TotalFiles}, ValidFiles={ValidFiles}, CorruptFiles={CorruptFiles}",
                    runId, processedFiles, finalValidFiles, corruptFiles);

                await endDbClient.Ctx.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Media integrity check was cancelled after processing {ProcessedFiles} files", processedFiles);
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"cancelled:{runId}");

            // Update run record for cancellation with actual progress made
            try
            {
                await using var cancelDbContext = new DavDatabaseContext();
                var cancelDbClient = new DavDatabaseClient(cancelDbContext);

                var cancelledRun = await cancelDbClient.Ctx.IntegrityCheckRuns
                    .FirstOrDefaultAsync(r => r.RunId == runId);
                if (cancelledRun != null)
                {
                    var finalValidFiles = processedFiles - corruptFiles;

                    cancelledRun.EndTime = DateTime.UtcNow;
                    cancelledRun.IsRunning = false;
                    cancelledRun.Status = IntegrityCheckRun.StatusOption.Cancelled;
                    cancelledRun.TotalFiles = processedFiles; // Actual files processed before cancellation
                    cancelledRun.ValidFiles = finalValidFiles;
                    cancelledRun.CorruptFiles = corruptFiles;
                    cancelledRun.CurrentFile = null;
                    cancelledRun.ProgressPercentage = null;

                    Log.Information("Cancelled run {RunId}: TotalFiles={TotalFiles}, ValidFiles={ValidFiles}, CorruptFiles={CorruptFiles}",
                        runId, processedFiles, finalValidFiles, corruptFiles);

                    await cancelDbClient.Ctx.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception storeEx)
            {
                Log.Warning(storeEx, "Failed to update run record for cancelled run {RunId}", runId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during media integrity check after processing {ProcessedFiles} files", processedFiles);
            _ = _websocketManager.SendMessage(WebsocketTopic.IntegrityCheckProgress, $"failed: {ex.Message}:{runId}");

            // Update run record for failure with actual progress made
            try
            {
                await using var failDbContext = new DavDatabaseContext();
                var failDbClient = new DavDatabaseClient(failDbContext);

                var failedRun = await failDbClient.Ctx.IntegrityCheckRuns
                    .FirstOrDefaultAsync(r => r.RunId == runId);
                if (failedRun != null)
                {
                    var finalValidFiles = processedFiles - corruptFiles;

                    failedRun.EndTime = DateTime.UtcNow;
                    failedRun.IsRunning = false;
                    failedRun.Status = IntegrityCheckRun.StatusOption.Failed;
                    failedRun.TotalFiles = processedFiles; // Actual files processed before failure
                    failedRun.ValidFiles = finalValidFiles;
                    failedRun.CorruptFiles = corruptFiles;
                    failedRun.CurrentFile = null;
                    failedRun.ProgressPercentage = null;

                    Log.Information("Failed run {RunId}: TotalFiles={TotalFiles}, ValidFiles={ValidFiles}, CorruptFiles={CorruptFiles}",
                        runId, processedFiles, finalValidFiles, corruptFiles);

                    await failDbClient.Ctx.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception storeEx)
            {
                Log.Warning(storeEx, "Failed to update run record for failed run {RunId}", runId);
            }
        }
    }

    private async Task<List<DavItem>> GetDavItemsToCheckAsync(DavDatabaseClient dbClient, int maxFiles, CancellationToken ct)
    {
        var cutoffTime = DateTime.UtcNow.AddDays(-_configManager.GetIntegrityCheckRecheckEligibilityDays());

        // Get all media files (NzbFile and RarFile types) that haven't been checked recently
        var query = dbClient.Ctx.Items
            .Where(item => (item.Type == DavItem.ItemType.NzbFile || item.Type == DavItem.ItemType.RarFile))
            .Where(item => item.FileSize > 0); // Only check files with actual content

        // Filter out recently checked files using the new IntegrityCheckFileResults table
        var recentlyCheckedFileIds = await dbClient.Ctx.IntegrityCheckFileResults
            .Where(r => r.LastChecked > cutoffTime && !r.IsLibraryFile)
            .Select(r => r.FileId)
            .Distinct()
            .ToListAsync(ct);

        if (recentlyCheckedFileIds.Any())
        {
            query = query.Where(item => !recentlyCheckedFileIds.Contains(item.Id.ToString()));
        }

        return await query.Take(maxFiles).ToListAsync(ct);
    }

    private async Task<List<IntegrityCheckItem>> GetLibraryIntegrityCheckItemsAsync(DavDatabaseClient dbClient, string libraryDir, int maxFiles, CancellationToken ct)
    {
        var checkItems = new List<IntegrityCheckItem>();
        var cutoffTime = DateTime.UtcNow.AddDays(-_configManager.GetIntegrityCheckRecheckEligibilityDays());

        try
        {
            // Get media files in library directory recursively (use enumerable for efficiency)
            var allFiles = Directory.EnumerateFiles(libraryDir, "*", SearchOption.AllDirectories)
                .Where(file => IsMediaFile(Path.GetExtension(file).ToLowerInvariant()));

            Log.Information("Scanning library directory for media files...");

            // Get recently checked library files from the new table
            var recentlyCheckedPaths = await dbClient.Ctx.IntegrityCheckFileResults
                .Where(r => r.LastChecked > cutoffTime && r.IsLibraryFile)
                .Select(r => r.FilePath)
                .Distinct()
                .ToListAsync(ct);

            var totalProcessed = 0;

            // Resolve symlinks to DavItems and filter out recently checked files
            foreach (var filePath in allFiles)
            {
                ct.ThrowIfCancellationRequested();
                totalProcessed++;

                try
                {
                    // Check if we should skip this file based on last check time
                    if (recentlyCheckedPaths.Contains(filePath))
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
                Log.Information("Skipped files are either: files checked recently, files imported outside nzbdav, deleted DavItems, or unsupported file types");
            }

            return checkItems;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error scanning library directory: {LibraryDir}", libraryDir);
            return checkItems;
        }
    }


    private async Task<(bool isCorrupt, string? errorMessage)> CheckFileIntegrityAsync(DavItem davItem, CancellationToken ct, string? libraryFilePath = null)
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
            // Use library file path for validation if available, otherwise fall back to DavItem path
            var pathForValidation = libraryFilePath ?? davItem.Path;
            var isValid = await FfprobeUtil.IsValidMediaStreamAsync(stream, pathForValidation, enableMp4DeepScan, ct);

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


    private async Task<IntegrityCheckFileResult.ActionOption> HandleCorruptFileAsync(DavDatabaseClient dbClient, DavItem? davItem, string filePath, CancellationToken ct)
    {
        var defaultAction = Enum.TryParse<IntegrityCheckRun.CorruptFileActionOption>(_configManager.GetCorruptFileAction(), true, out var action) ? action : IntegrityCheckRun.CorruptFileActionOption.Log;
        return await HandleCorruptFileAsync(dbClient, davItem, filePath, defaultAction, ct);
    }

    private async Task<IntegrityCheckFileResult.ActionOption> HandleCorruptFileAsync(DavDatabaseClient dbClient, DavItem? davItem, string filePath, IntegrityCheckRun.CorruptFileActionOption corruptFileAction, CancellationToken ct)
    {
        var isAutoMonitorEnabled = _configManager.IsAutoMonitorEnabled();

        Log.Information("HandleCorruptFileAsync: action={Action}, autoMonitorEnabled={AutoMonitorEnabled}, filePath={FilePath}",
            corruptFileAction, isAutoMonitorEnabled, filePath);

        // Auto-monitor corrupt files before deletion if enabled (for re-download)
        if (isAutoMonitorEnabled && (corruptFileAction == IntegrityCheckRun.CorruptFileActionOption.Delete || corruptFileAction == IntegrityCheckRun.CorruptFileActionOption.DeleteViaArr))
        {
            await _arrManager.MonitorFileInArrAsync(filePath, ct);
        }

        switch (corruptFileAction)
        {
            case IntegrityCheckRun.CorruptFileActionOption.Delete:
                return await HandleDeleteFileAsync(dbClient, davItem, filePath, ct);

            case IntegrityCheckRun.CorruptFileActionOption.DeleteViaArr:
                return await HandleDeleteViaArrAsync(dbClient, davItem, filePath, ct);

            case IntegrityCheckRun.CorruptFileActionOption.Log:
            default:
                // Just log the issue (already done above)
                return IntegrityCheckFileResult.ActionOption.None;
        }
    }

    private async Task<IntegrityCheckFileResult.ActionOption> HandleDeleteViaArrAsync(DavDatabaseClient dbClient, DavItem? davItem, string filePath, CancellationToken ct)
    {
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
                return IntegrityCheckFileResult.ActionOption.FileDeletedViaArr;
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
                    return IntegrityCheckFileResult.ActionOption.DeleteFailedDirectFallback;
                }
                else
                {
                    Log.Information("Direct deletion fallback is disabled, leaving corrupt file in place: {FilePath}", filePath);
                    Log.Warning("Corrupt file was not deleted by Radarr/Sonarr and direct deletion fallback is disabled. File remains: {FilePath}", filePath);
                    return IntegrityCheckFileResult.ActionOption.DeleteFailedNoFallback;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting corrupt file via Radarr/Sonarr: {FilePath}", filePath);
            return IntegrityCheckFileResult.ActionOption.DeleteError;
        }
    }

    private static async Task<IntegrityCheckFileResult.ActionOption> HandleDeleteFileAsync(DavDatabaseClient dbClient, DavItem? davItem, string filePath, CancellationToken ct)
    {
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
            return IntegrityCheckFileResult.ActionOption.FileDeletedSuccessfully;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting corrupt file: {FilePath}", filePath);
            return IntegrityCheckFileResult.ActionOption.DeleteError;
        }
    }

    private async Task<(bool shouldRun, int nextCheckInMinutes)> ShouldRunBackgroundCheckWithTimingAsync(CancellationToken ct)
    {
        try
        {
            var intervalHours = _configManager.GetIntegrityCheckIntervalHours();

            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            // Get the most recent check time from the new table
            var mostRecentCheck = await dbClient.Ctx.IntegrityCheckFileResults
                .OrderByDescending(r => r.LastChecked)
                .FirstOrDefaultAsync(ct);

            if (mostRecentCheck == null)
            {
                Log.Information("No previous integrity checks found, background check is due");
                return (true, 0); // No previous checks, so run now
            }

            var lastCheckTime = mostRecentCheck.LastChecked;

            var timeSinceLastCheck = DateTime.UtcNow - lastCheckTime.ToUniversalTime();
            var hoursUntilNext = intervalHours - timeSinceLastCheck.TotalHours;

            Log.Debug("Background check evaluation: Last check {LastCheck}, hours since: {HoursSince:F1}, interval: {Interval}h, hours until next: {UntilNext:F1}",
                lastCheckTime, timeSinceLastCheck.TotalHours, intervalHours, hoursUntilNext);

            var shouldRun = hoursUntilNext <= 0;
            var nextCheckMinutes = (int)Math.Max(hoursUntilNext * 60, 0); // Convert to minutes and don't return negative values

            return (shouldRun, nextCheckMinutes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error determining if background check should run");
            return (false, 10); // Don't run on error, retry in 10 minutes
        }
    }

    private async Task UpdateRunProgressAsync(string runId, int validFiles, int corruptFiles, string? currentFile, double? progressPercentage, CancellationToken ct, int? totalFiles = null)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            var run = await dbClient.Ctx.IntegrityCheckRuns
                .FirstOrDefaultAsync(r => r.RunId == runId, ct);

            if (run != null)
            {
                run.ValidFiles = validFiles;
                run.CorruptFiles = corruptFiles;
                run.CurrentFile = currentFile;
                run.ProgressPercentage = progressPercentage;

                // Set total files if provided (only on first update)
                if (totalFiles.HasValue && run.TotalFiles == 0)
                {
                    run.TotalFiles = totalFiles.Value;
                }

                await dbClient.Ctx.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation during progress update is expected during check cancellation
            // Don't log this - it's normal behavior
            // This includes TaskCanceledException which derives from OperationCanceledException
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update run progress for {RunId}", runId);
        }
    }

    private async Task StoreFileResultAsync(DavDatabaseClient dbClient, string filePath, string fileId, bool isLibraryFile, bool isValid, string? errorMessage, IntegrityCheckFileResult.ActionOption? actionTaken, string runId, CancellationToken ct)
    {
        try
        {
            var fileName = Path.GetFileName(filePath);
            var status = isValid ? IntegrityCheckFileResult.StatusOption.Valid : IntegrityCheckFileResult.StatusOption.Corrupt;

            var fileResult = new IntegrityCheckFileResult
            {
                RunId = runId,
                FileId = fileId,
                FilePath = filePath,
                FileName = fileName,
                IsLibraryFile = isLibraryFile,
                LastChecked = DateTime.UtcNow,
                Status = status,
                ErrorMessage = errorMessage,
                ActionTaken = actionTaken
            };

            dbClient.Ctx.IntegrityCheckFileResults.Add(fileResult);
            await dbClient.Ctx.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation during database save is expected during check cancellation
            // Don't log this as an error - just rethrow to propagate cancellation
            // This includes TaskCanceledException which derives from OperationCanceledException
            throw;
        }
    }

    private async Task CleanupOldIntegrityRunsAsync(DavDatabaseClient dbClient, CancellationToken ct)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-7);

            // Get old runs that are older than 7 days
            var oldRuns = await dbClient.Ctx.IntegrityCheckRuns
                .Where(r => r.StartTime < cutoffDate)
                .ToListAsync(ct);

            if (oldRuns.Count > 0)
            {
                Log.Information("Cleaning up {Count} integrity check runs older than 7 days", oldRuns.Count);

                // Delete associated file results first (foreign key constraint)
                var oldRunIds = oldRuns.Select(r => r.RunId).ToList();
                var oldFileResults = await dbClient.Ctx.IntegrityCheckFileResults
                    .Where(f => oldRunIds.Contains(f.RunId))
                    .ToListAsync(ct);

                if (oldFileResults.Count > 0)
                {
                    dbClient.Ctx.IntegrityCheckFileResults.RemoveRange(oldFileResults);
                    Log.Debug("Removing {Count} associated file results", oldFileResults.Count);
                }

                // Then delete the runs
                dbClient.Ctx.IntegrityCheckRuns.RemoveRange(oldRuns);

                await dbClient.Ctx.SaveChangesAsync(ct);
                Log.Information("Successfully cleaned up {RunCount} old runs and {FileCount} associated file results",
                    oldRuns.Count, oldFileResults.Count);
            }
            else
            {
                Log.Debug("No integrity check runs older than 7 days found for cleanup");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clean up old integrity check runs - continuing with new run");
            // Don't throw - cleanup failure shouldn't prevent new runs
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _semaphore?.Dispose();
    }
}
