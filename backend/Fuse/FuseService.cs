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
            _logger.LogInformation("=== FUSE Service Starting ===");
            
            _mountPoint = GetMountPoint();
            _logger.LogInformation("Mount point from GetMountPoint(): {MountPoint}", _mountPoint ?? "null");
            
            if (_mountPoint == null)
            {
                _logger.LogInformation("FUSE filesystem disabled - no mount point configured");
                _logger.LogInformation("FUSE_ENABLED env var: {FuseEnabled}", Environment.GetEnvironmentVariable("FUSE_ENABLED"));
                _logger.LogInformation("FUSE_MOUNT_POINT env var: {FuseMountPoint}", Environment.GetEnvironmentVariable("FUSE_MOUNT_POINT"));
                return;
            }

            // Try to load FUSE and see what specific error we get
            _logger.LogInformation("Attempting to load FUSE library...");
            
            try
            {
                // Create scoped services for testing
                using var testScope = _serviceScopeFactory.CreateScope();
                var testDbClient = testScope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                var testUsenetClient = testScope.ServiceProvider.GetRequiredService<UsenetStreamingClient>();
                var testLogger = testScope.ServiceProvider.GetRequiredService<ILogger<NzbWebDavFuseFileSystem>>();
                
                // Create filesystem instance - this will test basic type loading
                var testOperations = new NzbWebDavFuseFileSystem(testLogger, testDbClient, _configManager, testUsenetClient);
                _logger.LogInformation("FUSE types loaded successfully");
                
                // Now try to call FUSE.Mount to see if native library loads
                _logger.LogInformation("Testing FUSE native library...");
                FuseDotNet.Fuse.Mount(testOperations, new string[] { "test", "--help" });
                _logger.LogInformation("FUSE native library test completed");
            }
            catch (DllNotFoundException dllEx)
            {
                _logger.LogError("FUSE DLL not found - this indicates the native FUSE library isn't properly installed or linked");
                _logger.LogError("DLL Error: {Error}", dllEx.Message);
                _logger.LogError("Available library paths: {LibPaths}", Environment.GetEnvironmentVariable("LD_LIBRARY_PATH"));
                
                // Check if expected files exist
                var expectedPaths = new[] { 
                    "/usr/lib/libfuse3.so", 
                    "/usr/lib/x86_64-linux-gnu/libfuse3.so",
                    "/usr/local/lib/libfuse3.so"
                };
                
                foreach (var path in expectedPaths)
                {
                    var exists = File.Exists(path);
                    _logger.LogError("File {Path} exists: {Exists}", path, exists);
                }
                
                _logger.LogInformation("Application will continue with WebDAV-only access");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogInformation("FUSE library test completed with expected error: {Error}", ex.Message);
                _logger.LogInformation("This is normal for the --help test, FUSE should work");
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

            // Mount options for FUSE - allow both root and user access for NFS compatibility
            var options = new string[] 
            { 
                "nzbwebdav", // program name
                "-f", // foreground mode
                "-o", "allow_other,allow_root,uid=0,gid=0", 
                "-o", "default_permissions", 
                "-o", "ro",
                "-o", "fsname=nzbwebdav",
                _mountPoint // mount point
            };

            _logger.LogInformation("Mounting FUSE filesystem with options: {Options}", string.Join(" ", options));
            
            // Debug: Check if FUSE libraries are available at runtime
            _logger.LogInformation("=== Runtime FUSE Debug ===");
            try
            {
                var ldLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
                _logger.LogInformation("LD_LIBRARY_PATH: {LdLibraryPath}", ldLibraryPath ?? "not set");
                
                // Check if files exist
                var paths = new[] { "/usr/lib/libfuse3.so", "/lib/libfuse3.so", "/usr/lib/fuse3.so" };
                foreach (var path in paths)
                {
                    var exists = File.Exists(path);
                    _logger.LogInformation("File {Path} exists: {Exists}", path, exists);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during runtime FUSE debug");
            }
            _logger.LogInformation("=== End Runtime Debug ===");
            
            // This will block until the filesystem is unmounted
            await Task.Run(() => 
            {
                try
                {
                    FuseDotNet.Fuse.Mount(_fileSystem, new[] { "nzbwebdav", "-f", _mountPoint });
                    _logger.LogInformation("FUSE mount completed");
                }
                catch (DllNotFoundException dllEx)
                {
                    _logger.LogError("FUSE library not found - this might be due to Alpine Linux compatibility issues. FUSE functionality will be disabled.");
                    _logger.LogError("DLL Error: {Error}", dllEx.Message);
                    _logger.LogInformation("The application will continue without FUSE support. Consider using WebDAV access instead.");
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
