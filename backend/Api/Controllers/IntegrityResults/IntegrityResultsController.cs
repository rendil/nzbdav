using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Api.Controllers.IntegrityResults;

[ApiController]
[Route("api/integrity-results")]
public class IntegrityResultsController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        // Get all integrity check related config items
        var integrityConfigs = await dbClient.Ctx.ConfigItems
            .Where(c => c.ConfigName.StartsWith("integrity.last_check.") || 
                       c.ConfigName.StartsWith("integrity.status.") ||
                       c.ConfigName.StartsWith("integrity.path.") ||
                       c.ConfigName.StartsWith("integrity.error.") ||
                       c.ConfigName.StartsWith("integrity.action.") ||
                       c.ConfigName.StartsWith("integrity.run_id.") ||
                       c.ConfigName.StartsWith("integrity.run_start.") ||
                       c.ConfigName.StartsWith("integrity.run_end."))
            .ToListAsync(HttpContext.RequestAborted);

        // Parse and group the results
        var fileResults = new List<IntegrityFileResult>();
        var lastCheckConfigs = integrityConfigs
            .Where(c => c.ConfigName.StartsWith("integrity.last_check."))
            .ToList();

        foreach (var lastCheckConfig in lastCheckConfigs)
        {
            // Extract file identifier from config name
            string fileId;
            bool isLibraryFile = false;
            
            if (lastCheckConfig.ConfigName.StartsWith("integrity.last_check.library."))
            {
                fileId = lastCheckConfig.ConfigName.Substring("integrity.last_check.library.".Length);
                isLibraryFile = true;
            }
            else if (lastCheckConfig.ConfigName.StartsWith("integrity.last_check."))
            {
                fileId = lastCheckConfig.ConfigName.Substring("integrity.last_check.".Length);
                isLibraryFile = false;
            }
            else
            {
                continue;
            }

            // Find corresponding status, path, error, action, and run_id configs
            var statusConfigName = isLibraryFile 
                ? $"integrity.status.library.{fileId}"
                : $"integrity.status.{fileId}";
            
            var pathConfigName = isLibraryFile 
                ? $"integrity.path.library.{fileId}"
                : null; // Internal DAV items don't use path config
                
            var errorConfigName = isLibraryFile 
                ? $"integrity.error.library.{fileId}"
                : $"integrity.error.{fileId}";
                
            var actionConfigName = isLibraryFile 
                ? $"integrity.action.library.{fileId}"
                : $"integrity.action.{fileId}";
                
            var runIdConfigName = isLibraryFile 
                ? $"integrity.run_id.library.{fileId}"
                : $"integrity.run_id.{fileId}";
            
            var statusConfig = integrityConfigs
                .FirstOrDefault(c => c.ConfigName == statusConfigName);
            
            var pathConfig = pathConfigName != null ? integrityConfigs
                .FirstOrDefault(c => c.ConfigName == pathConfigName) : null;
                
            var errorConfig = integrityConfigs
                .FirstOrDefault(c => c.ConfigName == errorConfigName);
                
            var actionConfig = integrityConfigs
                .FirstOrDefault(c => c.ConfigName == actionConfigName);
                
            var runIdConfig = integrityConfigs
                .FirstOrDefault(c => c.ConfigName == runIdConfigName);

            // Get file info
            string filePath = "Unknown";
            string fileName = "Unknown";
            
            if (!isLibraryFile && Guid.TryParse(fileId, out var davItemId))
            {
                // Internal DAV item
                var davItem = await dbClient.Ctx.Items
                    .FirstOrDefaultAsync(d => d.Id == davItemId, HttpContext.RequestAborted);
                if (davItem != null)
                {
                    filePath = davItem.Path;
                    fileName = davItem.Name;
                }
            }
            else if (isLibraryFile)
            {
                // Library file - use stored path if available
                if (pathConfig != null && !string.IsNullOrEmpty(pathConfig.ConfigValue))
                {
                    filePath = pathConfig.ConfigValue;
                    fileName = Path.GetFileName(pathConfig.ConfigValue);
                }
                else
                {
                    // Fallback for legacy entries without stored paths
                    filePath = $"Library file (hash: {fileId})";
                    fileName = $"Library file {fileId}";
                }
            }

            // Parse the timestamp (keep as UTC, let frontend handle local display)
            DateTime parsedLastChecked = DateTime.MinValue;
            if (!string.IsNullOrEmpty(lastCheckConfig.ConfigValue))
            {
                if (DateTime.TryParse(lastCheckConfig.ConfigValue, out var tempLastChecked))
                {
                    // Keep as UTC - frontend will handle local display
                    parsedLastChecked = tempLastChecked.ToUniversalTime();
                    Log.Debug("Parsed timestamp for {FileId}: {OriginalValue} -> {ParsedValue} (UTC)", 
                        fileId, lastCheckConfig.ConfigValue, parsedLastChecked);
                }
                else
                {
                    Log.Warning("Failed to parse timestamp for {FileId}: {Value}", fileId, lastCheckConfig.ConfigValue);
                }
            }

            var result = new IntegrityFileResult
            {
                FileId = fileId,
                FilePath = filePath,
                FileName = fileName,
                IsLibraryFile = isLibraryFile,
                LastChecked = parsedLastChecked,
                Status = statusConfig?.ConfigValue ?? "unknown",
                ErrorMessage = errorConfig?.ConfigValue,
                ActionTaken = actionConfig?.ConfigValue,
                RunId = runIdConfig?.ConfigValue
            };

            fileResults.Add(result);
        }

        // Sort by last checked (most recent first)
        fileResults = fileResults
            .OrderByDescending(r => r.LastChecked)
            .ToList();

        // Group by execution run ID
        var jobRuns = fileResults
            .Where(r => r.LastChecked != DateTime.MinValue && !string.IsNullOrEmpty(r.RunId))
            .GroupBy(r => r.RunId)
            .Select(g => {
                var files = g.OrderByDescending(f => f.LastChecked).ToList();
                var runId = g.Key!;
                
                // Get start and end times for this run
                var startTimeConfig = integrityConfigs
                    .FirstOrDefault(c => c.ConfigName == $"integrity.run_start.{runId}");
                var endTimeConfig = integrityConfigs
                    .FirstOrDefault(c => c.ConfigName == $"integrity.run_end.{runId}");
                
                DateTime? startTime = null;
                DateTime? endTime = null;
                
                if (startTimeConfig != null && DateTime.TryParse(startTimeConfig.ConfigValue, out var parsedStart))
                {
                    startTime = parsedStart.ToUniversalTime();
                }
                
                if (endTimeConfig != null && DateTime.TryParse(endTimeConfig.ConfigValue, out var parsedEnd))
                {
                    endTime = parsedEnd.ToUniversalTime();
                }
                
                // Use start time as the run date, fallback to first file timestamp
                var runDate = startTime ?? files.First().LastChecked;
                
                Log.Debug("Creating job run for runId {RunId} on {RunDate} with {FileCount} files (Start: {StartTime}, End: {EndTime})", 
                    runId, runDate, g.Count(), startTime, endTime);
                
                return new IntegrityJobRun
                {
                    Date = runDate,
                    RunId = runId,
                    StartTime = startTime,
                    EndTime = endTime,
                    TotalFiles = g.Count(),
                    CorruptFiles = g.Count(f => f.Status == "corrupt"),
                    ValidFiles = g.Count(f => f.Status == "valid"),
                    Files = files
                };
            })
            .OrderByDescending(j => j.Date) // Order by most recent run first
            .ToList();

        var response = new IntegrityResultsResponse
        {
            JobRuns = jobRuns,
            AllFiles = fileResults
        };

        return Ok(response);
    }
}
