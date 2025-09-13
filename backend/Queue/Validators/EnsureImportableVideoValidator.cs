using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.Validators;

public class EnsureImportableVideoValidator(DavDatabaseClient dbClient, UsenetStreamingClient usenetClient)
{
    public async Task ThrowIfValidationFailsAsync(CancellationToken ct = default)
    {
        if (!await IsValidAsync(ct))
        {
            throw new NoVideoFilesFoundException("No importable videos found.");
        }
    }

    // Keep the old synchronous method for backward compatibility (filename-only check)
    public void ThrowIfValidationFails()
    {
        if (!IsValid())
        {
            throw new NoVideoFilesFoundException("No importable videos found.");
        }
    }

    private async Task<bool> IsValidAsync(CancellationToken ct)
    {
        var videoFiles = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .Where(x => FilenameUtil.IsVideoFile(x.Name))
            .ToList();

        if (videoFiles.Count == 0)
        {
            Log.Information("No video files found by filename extension");
            return false;
        }

        Log.Information("Found {VideoFileCount} potential video files, validating content with ffprobe...", videoFiles.Count);

        // Check each video file with ffprobe to ensure it's actually valid video content
        var validVideoCount = 0;
        foreach (var videoFile in videoFiles)
        {
            try
            {
                var isValid = await ValidateVideoContentAsync(videoFile, ct);
                if (isValid)
                {
                    validVideoCount++;
                    Log.Debug("Video file validated: {FileName}", videoFile.Name);
                }
                else
                {
                    Log.Warning("Video file failed validation: {FileName} - not valid media content", videoFile.Name);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error validating video file: {FileName}", videoFile.Name);
                // If we can't validate, assume it's valid to avoid false negatives
                validVideoCount++;
            }
        }

        var hasValidVideos = validVideoCount > 0;
        Log.Information("Video validation complete: {ValidCount}/{TotalCount} files contain valid video content", 
            validVideoCount, videoFiles.Count);

        return hasValidVideos;
    }

    private bool IsValid()
    {
        return dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .Any(x => FilenameUtil.IsVideoFile(x.Name));
    }

    private async Task<bool> ValidateVideoContentAsync(DavItem videoFile, CancellationToken ct)
    {
        try
        {
            Stream stream;
            
            if (videoFile.Type == DavItem.ItemType.NzbFile)
            {
                var nzbFile = await dbClient.GetNzbFileAsync(videoFile.Id, ct);
                if (nzbFile == null)
                {
                    Log.Warning("Could not find NZB file data for {FileName} (ID: {Id})", videoFile.Name, videoFile.Id);
                    return false;
                }
                stream = usenetClient.GetFileStream(nzbFile.SegmentIds, videoFile.FileSize!.Value, 1); // Use 1 connection for validation
            }
            else if (videoFile.Type == DavItem.ItemType.RarFile)
            {
                var rarFile = await dbClient.Ctx.RarFiles.Where(x => x.Id == videoFile.Id).FirstOrDefaultAsync(ct);
                if (rarFile == null)
                {
                    Log.Warning("Could not find RAR file data for {FileName} (ID: {Id})", videoFile.Name, videoFile.Id);
                    return false;
                }
                stream = new RarFileStream(rarFile.RarParts, usenetClient, 1); // Use 1 connection for validation
            }
            else
            {
                Log.Debug("Skipping validation for unsupported file type: {FileName} (Type: {ItemType})", videoFile.Name, videoFile.Type);
                return true; // Assume valid for unsupported types
            }

            // Use a smaller sample size for download validation (5MB instead of 20MB)
            var isValid = await FfprobeUtil.IsValidMediaStreamAsync(stream, 5 * 1024 * 1024, ct);
            await stream.DisposeAsync();
            
            return isValid;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error validating video content for {FileName}", videoFile.Name);
            return false;
        }
    }
}