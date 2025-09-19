using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.LibraryDiagnostic;

[ApiController]
[Route("api/library-diagnostic")]
public class LibraryDiagnosticController(ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var libraryDir = configManager.GetLibraryDir();
        
        if (string.IsNullOrEmpty(libraryDir))
        {
            return Ok(new { Error = "Library directory not configured" });
        }

        var response = new
        {
            LibraryPath = libraryDir,
            DirectoryExists = Directory.Exists(libraryDir),
            DirectoryInfo = GetDirectoryInfo(libraryDir),
            FileAnalysis = AnalyzeDirectory(libraryDir)
        };

        return Ok(response);
    }

    private static object GetDirectoryInfo(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return new { Error = "Directory does not exist" };

            var dirInfo = new DirectoryInfo(path);
            return new
            {
                FullPath = dirInfo.FullName,
                Exists = dirInfo.Exists,
                CreationTime = dirInfo.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                LastWriteTime = dirInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                Attributes = dirInfo.Attributes.ToString(),
                Parent = dirInfo.Parent?.FullName ?? "No parent"
            };
        }
        catch (Exception ex)
        {
            return new { Error = ex.Message };
        }
    }

    private static object AnalyzeDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return new { Error = "Directory does not exist" };

            var allFiles = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();
            var mediaExtensions = new HashSet<string>
            {
                ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
                ".mp3", ".flac", ".wav", ".aac", ".ogg", ".wma", ".m4a"
            };

            var mediaFiles = allFiles.Where(file => 
                mediaExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())).ToList();

            var subdirectories = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).ToList();

            // Sample files for debugging
            var sampleFiles = allFiles.Take(10).Select(f => new
            {
                Path = f,
                Extension = Path.GetExtension(f),
                Size = new FileInfo(f).Length,
                IsMedia = mediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
            }).ToList();

            // Sample directories
            var sampleDirs = subdirectories.Take(10).ToList();

            return new
            {
                TotalFiles = allFiles.Count,
                MediaFiles = mediaFiles.Count,
                Subdirectories = subdirectories.Count,
                SampleFiles = sampleFiles,
                SampleDirectories = sampleDirs,
                MediaFilesByExtension = mediaFiles.GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.Count()),
                TopLevelItems = Directory.EnumerateFileSystemEntries(path).Take(20).Select(item => new
                {
                    Name = Path.GetFileName(item),
                    FullPath = item,
                    IsDirectory = Directory.Exists(item),
                    IsFile = System.IO.File.Exists(item),
                    Extension = Path.GetExtension(item)
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            return new { Error = ex.Message };
        }
    }
}
