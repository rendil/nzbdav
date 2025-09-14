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
        Log.Information("Starting enhanced video content validation...");
        
        if (!await IsValidAsync(ct))
        {
            Log.Error("Video validation FAILED - No importable videos found. Throwing NoVideoFilesFoundException.");
            throw new NoVideoFilesFoundException("No importable videos found.");
        }
        
        Log.Information("Video validation PASSED - Valid video content found.");
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
            Log.Warning("No video files found by filename extension - this should cause validation to fail");
            return false;
        }

        Log.Information("Found {VideoFileCount} potential video files, validating content with ffprobe...", videoFiles.Count);
        
        // Log the files we found for debugging
        foreach (var file in videoFiles)
        {
            Log.Information("📁 Found potential video file: {FileName} (Type: {ItemType}, Size: {FileSize} bytes, Extension: {Extension})", 
                file.Name, file.Type, file.FileSize, Path.GetExtension(file.Name).ToLowerInvariant());
        }

        // Check each video file with ffprobe to ensure it's actually valid video content
        var validVideoCount = 0;
        foreach (var videoFile in videoFiles)
        {
            try
            {
                Log.Information("Validating video content for file: {FileName}", videoFile.Name);
                var isValid = await ValidateVideoContentAsync(videoFile, ct);
                if (isValid)
                {
                    validVideoCount++;
                    Log.Information("✅ Video file validation PASSED: {FileName}", videoFile.Name);
                }
                else
                {
                    Log.Warning("❌ Video file validation FAILED: {FileName} - not valid media content", videoFile.Name);
                }
            }
            catch (UsenetArticleNotFoundException ex)
            {
                Log.Warning("❌ Missing usenet articles for video file: {FileName} - {Message}", videoFile.Name, ex.Message);
                // Missing articles mean the file is incomplete/corrupt, don't count as valid
                // This is consistent with how the download process handles missing articles
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Error validating video file: {FileName} - treating as INVALID due to validation failure", videoFile.Name);
                // If we can't validate due to errors, treat as invalid to prevent false positives
                // This is more conservative but prevents downloads that Radarr can't import
            }
        }

        var hasValidVideos = validVideoCount > 0;
        
        if (hasValidVideos)
        {
            Log.Information("✅ Video validation PASSED: {ValidCount}/{TotalCount} files contain valid video content", 
                validVideoCount, videoFiles.Count);
        }
        else
        {
            Log.Error("❌ Video validation FAILED: {ValidCount}/{TotalCount} files contain valid video content - no importable videos found", 
                validVideoCount, videoFiles.Count);
            
            // Log specific reasons for failure
            if (videoFiles.Count == 0)
            {
                Log.Error("   Reason: No video files found by filename extension");
            }
            else
            {
                Log.Error("   Reason: All {FileCount} video files failed ffprobe validation or had missing articles", videoFiles.Count);
            }
        }

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

            // Use more thorough sample sizes for download validation (5MB then 25MB for better accuracy)
            var isValid = await FfprobeUtil.IsValidMediaStreamAsync(stream, videoFile.Name, 5 * 1024 * 1024, 25 * 1024 * 1024, ct);
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