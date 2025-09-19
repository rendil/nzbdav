using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Config;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.IntegrityDiagnostic;

[ApiController]
[Route("api/integrity-diagnostic")]
public class IntegrityDiagnosticController(ConfigManager configManager, DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        // Get current configuration
        var isEnabled = configManager.IsIntegrityCheckingEnabled();
        var intervalHours = configManager.GetIntegrityCheckIntervalHours();
        var intervalDays = configManager.GetIntegrityCheckIntervalDays();
        var maxFiles = configManager.GetMaxFilesToCheckPerRun();
        var corruptAction = configManager.GetCorruptFileAction();
        var libraryDir = configManager.GetLibraryDir();

        // Get last check information
        var lastCheckConfigs = await dbClient.Ctx.ConfigItems
            .Where(c => c.ConfigName.StartsWith("integrity.last_check."))
            .OrderByDescending(c => c.ConfigValue)
            .Take(10)
            .ToListAsync(HttpContext.RequestAborted);

        var lastCheckInfo = lastCheckConfigs.Select(c => new
        {
            ConfigName = c.ConfigName,
            LastCheck = DateTime.TryParse(c.ConfigValue, out var date) ? date.ToString("yyyy-MM-dd HH:mm:ss") : c.ConfigValue,
            DaysAgo = DateTime.TryParse(c.ConfigValue, out var dt) ? (DateTime.Now - dt).TotalDays.ToString("F1") : "unknown"
        }).ToList();

        // Check if library directory exists
        var libraryDirExists = !string.IsNullOrEmpty(libraryDir) && Directory.Exists(libraryDir);
        var libraryFileCount = 0;
        if (libraryDirExists && !string.IsNullOrEmpty(libraryDir))
        {
            try
            {
                libraryFileCount = Directory.EnumerateFiles(libraryDir, "*", SearchOption.AllDirectories)
                    .Count(file => IsMediaFile(Path.GetExtension(file).ToLowerInvariant()));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error accessing library directory: {ex.Message}");
                libraryFileCount = -1; // Error accessing directory
            }
        }

        // Calculate next expected check time
        var mostRecentCheck = lastCheckConfigs
            .Select(c => DateTime.TryParse(c.ConfigValue, out var date) ? date : DateTime.MinValue)
            .Where(d => d != DateTime.MinValue)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        var nextExpectedCheck = mostRecentCheck != DateTime.MinValue 
            ? mostRecentCheck.AddHours(intervalHours)
            : DateTime.Now; // If no previous checks, should run immediately

        // Calculate eligible files for next check
        var eligibleFileCount = 0;
        if (libraryDirExists && libraryFileCount > 0 && !string.IsNullOrEmpty(libraryDir))
        {
            try
            {
                var cutoffTime = DateTime.Now.AddDays(-intervalDays);
                var allFiles = Directory.EnumerateFiles(libraryDir, "*", SearchOption.AllDirectories)
                    .Where(file => IsMediaFile(Path.GetExtension(file).ToLowerInvariant()));

                foreach (var filePath in allFiles)
                {
                    var fileHash = GetFilePathHash(filePath);
                    var lastCheckConfig = $"integrity.last_check.library.{fileHash}";
                    
                    var lastCheckValue = await dbClient.Ctx.ConfigItems
                        .Where(c => c.ConfigName == lastCheckConfig)
                        .Select(c => c.ConfigValue)
                        .FirstOrDefaultAsync(HttpContext.RequestAborted);

                    if (lastCheckValue == null || !DateTime.TryParse(lastCheckValue, out var lastCheck) || lastCheck <= cutoffTime)
                    {
                        eligibleFileCount++;
                        if (eligibleFileCount >= maxFiles) break; // Limit for performance
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error calculating eligible file count: {ex.Message}");
                eligibleFileCount = -1; // Error calculating
            }
        }

        var response = new
        {
            Configuration = new
            {
                IsEnabled = isEnabled,
                IntervalHours = intervalHours,
                IntervalDays = intervalDays,
                MaxFiles = maxFiles,
                CorruptAction = corruptAction,
                LibraryDir = libraryDir ?? "Not configured"
            },
            LibraryStatus = new
            {
                DirectoryExists = libraryDirExists,
                FileCount = libraryFileCount,
                EligibleFileCount = eligibleFileCount,
                Error = libraryFileCount == -1 ? "Error accessing library directory" : null
            },
            SchedulingInfo = new
            {
                MostRecentCheck = mostRecentCheck != DateTime.MinValue ? mostRecentCheck.ToString("yyyy-MM-dd HH:mm:ss") : "Never",
                NextExpectedCheck = nextExpectedCheck.ToString("yyyy-MM-dd HH:mm:ss"),
                HoursUntilNext = (nextExpectedCheck - DateTime.Now).TotalHours,
                IsOverdue = DateTime.Now > nextExpectedCheck && isEnabled,
                CutoffTime = DateTime.Now.AddDays(-intervalDays).ToString("yyyy-MM-dd HH:mm:ss")
            },
            RecentChecks = lastCheckInfo,
            SystemTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Recommendations = GetRecommendations(isEnabled, libraryDirExists, mostRecentCheck, intervalHours, eligibleFileCount, intervalDays)
        };

        return Ok(response);
    }

    private static bool IsMediaFile(string extension)
    {
        var mediaExtensions = new HashSet<string>
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
            ".mp3", ".flac", ".wav", ".aac", ".ogg", ".wma", ".m4a"
        };
        return mediaExtensions.Contains(extension);
    }

    private static string GetFilePathHash(string filePath)
    {
        // Create a simple hash of the file path for unique identification
        return BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(filePath))).Replace("-", "")[..16];
    }

    private static List<string> GetRecommendations(bool isEnabled, bool libraryDirExists, DateTime mostRecentCheck, int intervalHours, int eligibleFileCount = 0, int intervalDays = 7)
    {
        var recommendations = new List<string>();

        if (!isEnabled)
        {
            recommendations.Add("Integrity checking is disabled. Enable it in Settings → Integrity.");
        }

        if (!libraryDirExists)
        {
            recommendations.Add("Library directory is not configured or doesn't exist. Configure it in Settings → Library.");
        }

        if (isEnabled && mostRecentCheck == DateTime.MinValue)
        {
            recommendations.Add("No integrity checks have been run yet. Try triggering a manual check in Settings → Integrity.");
        }

        if (isEnabled && mostRecentCheck != DateTime.MinValue)
        {
            var hoursSinceLastCheck = (DateTime.Now - mostRecentCheck).TotalHours;
            if (hoursSinceLastCheck > intervalHours * 2)
            {
                recommendations.Add($"Last check was {hoursSinceLastCheck:F1} hours ago, which is more than 2x the interval. Check logs for errors or restart the service.");
            }
        }

        if (isEnabled && eligibleFileCount == 0)
        {
            recommendations.Add($"No files are eligible for checking due to interval_days setting. Files checked within the last {intervalDays} days won't be rechecked. Consider reducing interval_days if you want more frequent checks.");
        }

        if (isEnabled && eligibleFileCount > 0)
        {
            recommendations.Add($"Background task should be running but {eligibleFileCount} files are eligible for checking. This suggests the background scheduler has stopped.");
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("Configuration looks good! Integrity checks should be running on schedule.");
        }

        return recommendations;
    }
}
