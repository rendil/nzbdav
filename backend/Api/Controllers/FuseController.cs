using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Fuse;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FuseController : ControllerBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConfigManager _configManager;

    public FuseController(IServiceProvider serviceProvider, ConfigManager configManager)
    {
        _serviceProvider = serviceProvider;
        _configManager = configManager;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var isEnabled = _configManager.IsFuseEnabled();
        var mountPoint = _configManager.GetFuseMountPoint();
        
        string? activeMountPoint = null;
        bool isMounted = false;
        
        if (isEnabled)
        {
            var fuseService = _serviceProvider.GetService<FuseService>();
            if (fuseService != null)
            {
                activeMountPoint = fuseService.GetActiveMountPoint();
                isMounted = fuseService.IsFilesystemMounted();
            }
        }

        return Ok(new
        {
            enabled = isEnabled,
            configured_mount_point = mountPoint,
            active_mount_point = activeMountPoint,
            is_mounted = isMounted,
            status = GetStatusText(isEnabled, isMounted, activeMountPoint)
        });
    }

    [HttpGet("mount-info")]
    public IActionResult GetMountInfo()
    {
        if (!_configManager.IsFuseEnabled())
        {
            return Ok(new { message = "FUSE filesystem is disabled" });
        }

        var fuseService = _serviceProvider.GetService<FuseService>();
        var mountPoint = fuseService?.GetActiveMountPoint();
        if (mountPoint == null)
        {
            return Ok(new { message = "FUSE filesystem is not active" });
        }

        try
        {
            var directoryExists = Directory.Exists(mountPoint);
            var canRead = false;
            var canList = false;
            string[] contents = Array.Empty<string>();

            if (directoryExists)
            {
                try
                {
                    contents = Directory.GetDirectories(mountPoint)
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrEmpty(name))
                        .ToArray()!;
                    canList = true;
                    canRead = true;
                }
                catch
                {
                    // Access denied or other error
                }
            }

            return Ok(new
            {
                mount_point = mountPoint,
                directory_exists = directoryExists,
                can_read = canRead,
                can_list = canList,
                contents = contents,
                instructions = new
                {
                    access = $"You can access the files at: {mountPoint}",
                    nfs_setup = "To share via NFS, use the provided Docker Compose configuration",
                    unmount = $"To unmount: fusermount -u {mountPoint}"
                }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static string GetStatusText(bool isEnabled, bool isMounted, string? activeMountPoint)
    {
        if (!isEnabled)
            return "Disabled";
        
        if (isMounted && activeMountPoint != null)
            return $"Active at {activeMountPoint}";
        
        if (activeMountPoint != null)
            return "Starting...";
        
        return "Configuration error";
    }
}
