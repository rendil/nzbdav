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
                       c.ConfigName.StartsWith("integrity.action."))
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

            // Find corresponding status, path, error, and action configs
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
            
            var statusConfig = integrityConfigs
                .FirstOrDefault(c => c.ConfigName == statusConfigName);
            
            var pathConfig = pathConfigName != null ? integrityConfigs
                .FirstOrDefault(c => c.ConfigName == pathConfigName) : null;
                
            var errorConfig = integrityConfigs
                .FirstOrDefault(c => c.ConfigName == errorConfigName);
                
            var actionConfig = integrityConfigs
                .FirstOrDefault(c => c.ConfigName == actionConfigName);

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

            // Parse the timestamp more carefully and ensure local time for consistent grouping
            DateTime parsedLastChecked = DateTime.MinValue;
            if (!string.IsNullOrEmpty(lastCheckConfig.ConfigValue))
            {
                if (DateTime.TryParse(lastCheckConfig.ConfigValue, out var tempLastChecked))
                {
                    // Convert to local time to ensure consistent date grouping
                    parsedLastChecked = tempLastChecked.ToLocalTime();
                    Log.Debug("Parsed timestamp for {FileId}: {OriginalValue} -> {ParsedValue} (Local Date: {DatePart})", 
                        fileId, lastCheckConfig.ConfigValue, parsedLastChecked, parsedLastChecked.Date);
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
                ActionTaken = actionConfig?.ConfigValue
            };

            fileResults.Add(result);
        }

        // Sort by last checked (most recent first)
        fileResults = fileResults
            .OrderByDescending(r => r.LastChecked)
            .ToList();

        // Group by date for job runs (use local date to match what users see)
        var jobRuns = fileResults
            .Where(r => r.LastChecked != DateTime.MinValue)
            .GroupBy(r => {
                // Ensure we're grouping by local date only (strip time component)
                var localDate = r.LastChecked.ToLocalTime().Date;
                Log.Debug("Grouping file {FileName} with timestamp {LastChecked} under date {GroupDate}", 
                    r.FileName, r.LastChecked, localDate);
                return localDate;
            })
            .Select(g => {
                Log.Debug("Creating job run for date {Date} with {FileCount} files", g.Key, g.Count());
                var files = g.OrderByDescending(f => f.LastChecked).ToList();
                
                // Log first few files to debug the grouping issue
                for (int i = 0; i < Math.Min(3, files.Count); i++)
                {
                    Log.Debug("  File {Index}: {FileName} checked at {LastChecked} (local: {LocalTime}) grouped under {GroupDate}", 
                        i + 1, files[i].FileName, files[i].LastChecked, files[i].LastChecked.ToLocalTime(), g.Key);
                }
                
                return new IntegrityJobRun
                {
                    Date = g.Key,
                    TotalFiles = g.Count(),
                    CorruptFiles = g.Count(f => f.Status == "corrupt"),
                    ValidFiles = g.Count(f => f.Status == "valid"),
                    Files = files
                };
            })
            .OrderByDescending(j => j.Date)
            .ToList();

        var response = new IntegrityResultsResponse
        {
            JobRuns = jobRuns,
            AllFiles = fileResults
        };

        return Ok(response);
    }
}
