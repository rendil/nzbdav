using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Clients;

namespace NzbWebDAV.Fuse;

/// <summary>
/// Hosted service that manages the FUSE filesystem lifecycle
/// </summary>
public class FuseService : BackgroundService
{
    private readonly ILogger<FuseService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ConfigManager _configManager;
    private NzbWebDavFuseFileSystem? _fileSystem;
    private string? _mountPoint;

    public FuseService(
        ILogger<FuseService> logger,
        IServiceScopeFactory serviceScopeFactory,
        ConfigManager configManager)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _configManager = configManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _mountPoint = GetMountPoint();
            if (_mountPoint == null)
            {
                _logger.LogInformation("FUSE filesystem disabled - no mount point configured");
                return;
            }

            _logger.LogInformation("Starting FUSE filesystem at mount point: {MountPoint}", _mountPoint);

            // Ensure mount point directory exists
            Directory.CreateDirectory(_mountPoint);

            // Create filesystem instance with scoped services
            using var scope = _serviceScopeFactory.CreateScope();
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
            var usenetClient = scope.ServiceProvider.GetRequiredService<UsenetStreamingClient>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<NzbWebDavFuseFileSystem>>();

            _fileSystem = new NzbWebDavFuseFileSystem(logger, dbClient, _configManager, usenetClient);

            // Mount options for FUSE
            var options = new string[] 
            { 
                "-f", // foreground mode
                "-o", "allow_other", // allow other users to access
                "-o", "default_permissions", // use default permission checking
                "-o", "fsname=nzbwebdav", // filesystem name
                "-o", "subtype=nzbwebdav", // filesystem subtype
                "-o", "ro" // read-only mount
            };

            _logger.LogInformation("Mounting FUSE filesystem...");
            
            // This will block until the filesystem is unmounted
            await Task.Run(() => 
            {
                try
                {
                    FuseDotNet.Fuse.Mount(_fileSystem, new[] { "nzbwebdav", "-f", _mountPoint });
                    _logger.LogInformation("FUSE mount completed");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error mounting FUSE filesystem");
                }
            }, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("FUSE service stopping due to cancellation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in FUSE service execution");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping FUSE service...");

        try
        {
            if (_mountPoint != null && Directory.Exists(_mountPoint))
            {
                _logger.LogInformation("Unmounting FUSE filesystem from: {MountPoint}", _mountPoint);
                
                // Try to unmount gracefully
                await Task.Run(() =>
                {
                    try
                    {
                        // Force unmount using system command
                        var process = new System.Diagnostics.Process
                        {
                            StartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "fusermount",
                                Arguments = $"-u \"{_mountPoint}\"",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            }
                        };

                        process.Start();
                        process.WaitForExit(5000); // Wait up to 5 seconds

                        if (process.ExitCode == 0)
                        {
                            _logger.LogInformation("Successfully unmounted FUSE filesystem");
                        }
                        else
                        {
                            _logger.LogWarning("fusermount returned exit code: {ExitCode}", process.ExitCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error during unmount, filesystem may still be mounted");
                    }
                }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping FUSE service");
        }

        await base.StopAsync(cancellationToken);
    }

    private string? GetMountPoint()
    {
        // Check configuration first
        var configuredPath = _configManager.GetConfigValue("fuse.mount-point");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        // Check environment variable
        var envPath = Environment.GetEnvironmentVariable("FUSE_MOUNT_POINT");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath;
        }

        // Default path if enabled
        var enabled = _configManager.GetConfigValue("fuse.enabled")?.ToLowerInvariant() == "true" ||
                     Environment.GetEnvironmentVariable("FUSE_ENABLED")?.ToLowerInvariant() == "true";

        if (enabled)
        {
            return "/mnt/nzbwebdav";
        }

        return null;
    }

    /// <summary>
    /// Gets the current mount point if the filesystem is active
    /// </summary>
    public string? GetActiveMountPoint()
    {
        return _fileSystem != null ? _mountPoint : null;
    }

    /// <summary>
    /// Checks if the FUSE filesystem is currently mounted and accessible
    /// </summary>
    public bool IsFilesystemMounted()
    {
        if (_mountPoint == null || _fileSystem == null)
            return false;

        try
        {
            // Try to check if the mount point is actually a FUSE mount
            var mountInfo = File.ReadAllText("/proc/mounts");
            return mountInfo.Contains($" {_mountPoint} fuse");
        }
        catch
        {
            return false;
        }
    }
}
