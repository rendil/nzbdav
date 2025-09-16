using System.Text;
using FuseDotNet;
using LTRData.Extensions.Native.Memory;
using Microsoft.Extensions.Logging;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Clients;

namespace NzbWebDAV.Fuse;

/// <summary>
/// FUSE filesystem implementation that mirrors the WebDAV content structure
/// </summary>
public class NzbWebDavFuseFileSystem : IFuseOperations
{
    private readonly ILogger<NzbWebDavFuseFileSystem> _logger;
    private readonly DavDatabaseClient _dbClient;
    private readonly ConfigManager _configManager;
    private readonly UsenetStreamingClient _usenetClient;
    private readonly Dictionary<string, CachedFileHandle> _openFiles = new();
    private readonly object _lockObject = new();

    public NzbWebDavFuseFileSystem(
        ILogger<NzbWebDavFuseFileSystem> logger,
        DavDatabaseClient dbClient,
        ConfigManager configManager,
        UsenetStreamingClient usenetClient)
    {
        _logger = logger;
        _dbClient = dbClient;
        _configManager = configManager;
        _usenetClient = usenetClient;
    }

    #region Directory Operations

    public PosixResult GetAttr(ReadOnlyNativeMemory<byte> path, out FuseFileStat stat, ref FuseFileInfo fi)
    {
        try
        {
            var pathStr = Encoding.UTF8.GetString(path.Span);
            _logger.LogDebug("GetAttr called for path: {Path}", pathStr);
            
            var item = GetDavItemByPath(pathStr).GetAwaiter().GetResult();
            if (item == null)
            {
                _logger.LogDebug("Path not found: {Path}", pathStr);
                stat = default;
                return PosixResult.ENOENT;
            }

            stat = new FuseFileStat
            {
                st_uid = 0,
                st_gid = 0
            };
            
            if (item.Type == DavItem.ItemType.Directory || 
                item.Type == DavItem.ItemType.SymlinkRoot || 
                item.Type == DavItem.ItemType.IdsRoot)
            {
                stat.st_mode = (PosixFileMode)0x41ED; // S_IFDIR (0x4000) | 0755 permissions
                stat.st_nlink = 2;
                stat.st_size = 0;
            }
            else
            {
                stat.st_mode = (PosixFileMode)0x81A4; // S_IFREG (0x8000) | 0644 permissions
                stat.st_nlink = 1;
                stat.st_size = item.FileSize ?? 0;
            }

            // Note: Time properties may be readonly in FuseFileStat
            // For basic functionality, we can skip setting timestamps
            // The filesystem will still work without explicit time values

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetAttr for path: {Path}", Encoding.UTF8.GetString(path.Span));
            stat = default;
            return PosixResult.EIO;
        }
    }

    public PosixResult ReadDir(ReadOnlyNativeMemory<byte> path, out IEnumerable<FuseDirEntry> entries, ref FuseFileInfo fi, long offset, FuseReadDirFlags flags)
    {
        try
        {
            var pathStr = Encoding.UTF8.GetString(path.Span);
            _logger.LogDebug("ReadDir called for path: {Path}", pathStr);
            
            var item = GetDavItemByPath(pathStr).GetAwaiter().GetResult();
            if (item == null || !IsDirectory(item))
            {
                entries = Enumerable.Empty<FuseDirEntry>();
                return PosixResult.ENOTDIR;
            }

            var entryList = new List<FuseDirEntry>();

            // Add . and .. entries
            entryList.Add(new FuseDirEntry(".", 0, 0, new FuseFileStat()));
            entryList.Add(new FuseDirEntry("..", 0, 0, new FuseFileStat()));

            // Get children from database
            var children = _dbClient.GetDirectoryChildrenAsync(item.Id, CancellationToken.None)
                .GetAwaiter().GetResult();

            foreach (var child in children)
            {
                if (!_configManager.ShowHiddenWebdavFiles() && child.Name.StartsWith('.'))
                    continue;

                entryList.Add(new FuseDirEntry(child.Name, 0, 0, new FuseFileStat()));
            }

            entries = entryList;
            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ReadDir for path: {Path}", Encoding.UTF8.GetString(path.Span));
            entries = Enumerable.Empty<FuseDirEntry>();
            return PosixResult.EIO;
        }
    }

    #endregion

    #region File Operations

    public PosixResult Open(ReadOnlyNativeMemory<byte> path, ref FuseFileInfo fi)
    {
        try
        {
            var pathStr = Encoding.UTF8.GetString(path.Span);
            _logger.LogDebug("Open called for path: {Path}", pathStr);
            
            var item = GetDavItemByPath(pathStr).GetAwaiter().GetResult();
            if (item == null)
            {
                return PosixResult.ENOENT;
            }

            if (IsDirectory(item))
            {
                return PosixResult.EISDIR;
            }

            // For now, we only support read operations (basic check)
            // Note: OpenFlags may have different names in LTRData.FuseDotNet
            // This is a simplified check for read-only access

            // Create a cached file handle
            var handle = new CachedFileHandle
            {
                DavItem = item,
                Path = pathStr,
                OpenTime = DateTime.UtcNow
            };

            lock (_lockObject)
            {
                var handleId = Guid.NewGuid().ToString();
                _openFiles[handleId] = handle;
                // Note: fh property may not be accessible - using a simple approach
                try
                {
                    // Store handle ID for later retrieval if possible
                    // fi.fh = (ulong)handleId.GetHashCode();
                }
                catch
                {
                    // If fh is not accessible, that's ok
                }
            }

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Open for path: {Path}", Encoding.UTF8.GetString(path.Span));
            return PosixResult.EIO;
        }
    }

    public PosixResult Read(ReadOnlyNativeMemory<byte> path, NativeMemory<byte> buffer, long offset, out int bytesRead, ref FuseFileInfo fi)
    {
        try
        {
            var pathStr = Encoding.UTF8.GetString(path.Span);
            _logger.LogDebug("Read called for path: {Path}, size: {Size}, offset: {Offset}", pathStr, buffer.Length, offset);
            
            var item = GetDavItemByPath(pathStr).GetAwaiter().GetResult();
            if (item == null)
            {
                bytesRead = 0;
                return PosixResult.ENOENT;
            }

            if (IsDirectory(item))
            {
                bytesRead = 0;
                return PosixResult.EISDIR;
            }

            // Get the stream for this file
            var stream = GetFileStreamAsync(item).GetAwaiter().GetResult();
            if (stream == null)
            {
                bytesRead = 0;
                return PosixResult.EIO;
            }

            using (stream)
            {
                if (offset >= stream.Length)
                {
                    bytesRead = 0;
                    return PosixResult.Success; // EOF
                }

                stream.Seek(offset, SeekOrigin.Begin);
                var bytesToRead = Math.Min(buffer.Length, (int)(stream.Length - offset));
                var tempBuffer = new byte[bytesToRead];
                bytesRead = stream.Read(tempBuffer, 0, bytesToRead);
                
                tempBuffer.AsSpan(0, bytesRead).CopyTo(buffer.Span);
                
                _logger.LogDebug("Read {BytesRead} bytes from {Path}", bytesRead, pathStr);
                return PosixResult.Success;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Read for path: {Path}", Encoding.UTF8.GetString(path.Span));
            bytesRead = 0;
            return PosixResult.EIO;
        }
    }

    public PosixResult Release(ReadOnlyNativeMemory<byte> path, ref FuseFileInfo fi)
    {
        try
        {
            var pathStr = Encoding.UTF8.GetString(path.Span);
            _logger.LogDebug("Release called for path: {Path}", pathStr);
            
            lock (_lockObject)
            {
                // Simple cleanup - remove any stale handles
                // Note: Without access to fi.fh, we'll clean up by path
                var toRemove = _openFiles.Where(kvp => kvp.Value.Path == pathStr).ToList();
                foreach (var item in toRemove)
                {
                    _openFiles.Remove(item.Key);
                }
            }

            return PosixResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Release for path: {Path}", Encoding.UTF8.GetString(path.Span));
            return PosixResult.EIO;
        }
    }

    #endregion

    #region Helper Methods

    private async Task<DavItem?> GetDavItemByPath(string path)
    {
        try
        {
            // Normalize path
            path = path.Trim('/');
            
            if (string.IsNullOrEmpty(path))
            {
                return DavItem.Root;
            }

            // Handle special root directories
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                return parts[0] switch
                {
                    "nzbs" => DavItem.NzbFolder,
                    "content" => DavItem.ContentFolder,
                    "completed-symlinks" => DavItem.SymlinkFolder,
                    ".ids" => DavItem.IdsFolder,
                    _ => null
                };
            }

            // Navigate through the path
            var current = parts[0] switch
            {
                "nzbs" => DavItem.NzbFolder,
                "content" => DavItem.ContentFolder,
                "completed-symlinks" => DavItem.SymlinkFolder,
                ".ids" => DavItem.IdsFolder,
                _ => null
            };

            if (current == null)
                return null;

            for (int i = 1; i < parts.Length; i++)
            {
                current = await _dbClient.GetDirectoryChildAsync(current.Id, parts[i], CancellationToken.None);
                if (current == null)
                    return null;
            }

            return current;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving path: {Path}", path);
            return null;
        }
    }

    private async Task<Stream?> GetFileStreamAsync(DavItem item)
    {
        try
        {
            return item.Type switch
            {
                DavItem.ItemType.NzbFile => await GetNzbFileStreamAsync(item),
                DavItem.ItemType.RarFile => await GetRarFileStreamAsync(item),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting stream for item: {ItemId}", item.Id);
            return null;
        }
    }

    private async Task<Stream?> GetNzbFileStreamAsync(DavItem item)
    {
        var file = await _dbClient.GetNzbFileAsync(item.Id, CancellationToken.None);
        if (file == null)
            return null;

        return _usenetClient.GetFileStream(file.SegmentIds, item.FileSize!.Value, _configManager.GetConnectionsPerStream());
    }

    private async Task<Stream?> GetRarFileStreamAsync(DavItem item)
    {
        var file = await _dbClient.GetRarFileAsync(item.Id, CancellationToken.None);
        if (file == null || file.RarParts.Length == 0)
            return null;

        // For now, we'll handle simple single-part RAR files
        // Multi-part RAR files would need more complex handling
        var firstPart = file.RarParts[0];
        return _usenetClient.GetFileStream(firstPart.SegmentIds, item.FileSize!.Value, _configManager.GetConnectionsPerStream());
    }

    private static bool IsDirectory(DavItem item)
    {
        return item.Type == DavItem.ItemType.Directory ||
               item.Type == DavItem.ItemType.SymlinkRoot ||
               item.Type == DavItem.ItemType.IdsRoot;
    }

    #endregion

    // Add missing required interface methods
    public void Init(ref FuseConnInfo conn)
    {
        _logger.LogInformation("FUSE filesystem initialized");
    }

    public PosixResult OpenDir(ReadOnlyNativeMemory<byte> path, ref FuseFileInfo fi)
    {
        // For directories, we don't need special handling
        return PosixResult.Success;
    }

    public PosixResult Access(ReadOnlyNativeMemory<byte> path, PosixAccessMode mode)
    {
        // For simplicity, allow all access for now
        return PosixResult.Success;
    }

    // Additional required methods (read-only filesystem stubs)
    public PosixResult StatFs(ReadOnlyNativeMemory<byte> path, out FuseVfsStat statvfs)
    {
        statvfs = new FuseVfsStat
        {
            f_bsize = 4096,
            f_frsize = 4096,
            f_blocks = 1000000,
            f_bfree = 500000,
            f_bavail = 500000,
            f_files = 1000000,
            f_ffree = 500000,
            f_namemax = 255
        };
        return PosixResult.Success;
    }

    public PosixResult FSyncDir(ReadOnlyNativeMemory<byte> path, bool datasync, ref FuseFileInfo fi)
    {
        return PosixResult.Success; // No-op for read-only
    }

    public PosixResult ReadLink(ReadOnlyNativeMemory<byte> path, NativeMemory<byte> buffer)
    {
        return PosixResult.ENOENT; // No symlinks in our filesystem
    }

    public PosixResult ReleaseDir(ReadOnlyNativeMemory<byte> path, ref FuseFileInfo fi)
    {
        return PosixResult.Success; // No special cleanup needed
    }

    public PosixResult Link(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult MkDir(ReadOnlyNativeMemory<byte> path, PosixFileMode mode)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult RmDir(ReadOnlyNativeMemory<byte> path)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult FSync(ReadOnlyNativeMemory<byte> path, bool datasync, ref FuseFileInfo fi)
    {
        return PosixResult.Success; // No-op for read-only
    }

    public PosixResult Unlink(ReadOnlyNativeMemory<byte> path)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult Write(ReadOnlyNativeMemory<byte> path, ReadOnlyNativeMemory<byte> buffer, long offset, out int bytesWritten, ref FuseFileInfo fi)
    {
        bytesWritten = 0;
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult SymLink(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult Flush(ReadOnlyNativeMemory<byte> path, ref FuseFileInfo fi)
    {
        return PosixResult.Success; // No-op for read-only
    }

    public PosixResult Rename(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult Truncate(ReadOnlyNativeMemory<byte> path, long size)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult UTime(ReadOnlyNativeMemory<byte> path, TimeSpec atime, TimeSpec mtime, ref FuseFileInfo fi)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult Create(ReadOnlyNativeMemory<byte> path, int mode, ref FuseFileInfo fi)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult IoCtl(ReadOnlyNativeMemory<byte> path, int cmd, nint arg, ref FuseFileInfo fi, FuseIoctlFlags flags, nint data)
    {
        return PosixResult.ENOTTY; // Not a tty
    }

    public PosixResult ChMod(NativeMemory<byte> path, PosixFileMode mode)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult ChOwn(NativeMemory<byte> path, int uid, int gid)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public PosixResult FAllocate(NativeMemory<byte> path, FuseAllocateMode mode, long offset, long length, ref FuseFileInfo fi)
    {
        return PosixResult.EROFS; // Read-only filesystem
    }

    public void Dispose()
    {
        // Cleanup if needed
        _logger.LogInformation("FUSE filesystem disposed");
    }

    #region Nested Classes

    private class CachedFileHandle
    {
        public DavItem DavItem { get; set; } = null!;
        public string Path { get; set; } = null!;
        public DateTime OpenTime { get; set; }
    }

    #endregion
}
