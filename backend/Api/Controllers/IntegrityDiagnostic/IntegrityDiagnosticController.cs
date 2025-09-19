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
        if (libraryDirExists)
        {
            try
            {
                libraryFileCount = Directory.EnumerateFiles(libraryDir, "*", SearchOption.AllDirectories)
                    .Count(file => IsMediaFile(Path.GetExtension(file).ToLowerInvariant()));
            }
            catch (Exception ex)
            {
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
                Error = libraryFileCount == -1 ? "Error accessing library directory" : null
            },
            SchedulingInfo = new
            {
                MostRecentCheck = mostRecentCheck != DateTime.MinValue ? mostRecentCheck.ToString("yyyy-MM-dd HH:mm:ss") : "Never",
                NextExpectedCheck = nextExpectedCheck.ToString("yyyy-MM-dd HH:mm:ss"),
                HoursUntilNext = (nextExpectedCheck - DateTime.Now).TotalHours,
                IsOverdue = DateTime.Now > nextExpectedCheck && isEnabled
            },
            RecentChecks = lastCheckInfo,
            SystemTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Recommendations = GetRecommendations(isEnabled, libraryDirExists, mostRecentCheck, intervalHours)
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

    private static List<string> GetRecommendations(bool isEnabled, bool libraryDirExists, DateTime mostRecentCheck, int intervalHours)
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
                recommendations.Add($"Last check was {hoursSinceLastCheck:F1} hours ago, which is more than 2x the interval. Check logs for errors.");
            }
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("Configuration looks good! Integrity checks should be running on schedule.");
        }

        return recommendations;
    }
}
