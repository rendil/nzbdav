using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

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
                       c.ConfigName.StartsWith("integrity.status."))
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

            // Find corresponding status config
            var statusConfigName = isLibraryFile 
                ? $"integrity.status.library.{fileId}"
                : $"integrity.status.{fileId}";
            
            var statusConfig = integrityConfigs
                .FirstOrDefault(c => c.ConfigName == statusConfigName);

            // Get file info
            string filePath = "Unknown";
            string fileName = "Unknown";
            
            if (!isLibraryFile && Guid.TryParse(fileId, out var davItemId))
            {
                // Internal DAV item
                var davItem = await dbClient.Ctx.DavItems
                    .FirstOrDefaultAsync(d => d.Id == davItemId, HttpContext.RequestAborted);
                if (davItem != null)
                {
                    filePath = davItem.Path;
                    fileName = davItem.Name;
                }
            }
            else if (isLibraryFile)
            {
                // Library file - we'd need to reverse the hash to get the path
                // For now, just use the hash as identifier
                filePath = $"Library file (hash: {fileId})";
                fileName = $"Library file {fileId}";
            }

            var result = new IntegrityFileResult
            {
                FileId = fileId,
                FilePath = filePath,
                FileName = fileName,
                IsLibraryFile = isLibraryFile,
                LastChecked = DateTime.TryParse(lastCheckConfig.ConfigValue, out var lastChecked) 
                    ? lastChecked 
                    : DateTime.MinValue,
                Status = statusConfig?.ConfigValue ?? "unknown"
            };

            fileResults.Add(result);
        }

        // Sort by last checked (most recent first)
        fileResults = fileResults
            .OrderByDescending(r => r.LastChecked)
            .ToList();

        // Group by date for job runs
        var jobRuns = fileResults
            .Where(r => r.LastChecked != DateTime.MinValue)
            .GroupBy(r => r.LastChecked.Date)
            .Select(g => new IntegrityJobRun
            {
                Date = g.Key,
                TotalFiles = g.Count(),
                CorruptFiles = g.Count(f => f.Status == "corrupt"),
                ValidFiles = g.Count(f => f.Status == "valid"),
                Files = g.ToList()
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
