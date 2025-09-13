using System.Diagnostics;
using System.Net.Sockets;
using Serilog;

namespace NzbWebDAV.Utils;

public static class FfprobeUtil
{
    /// <summary>
    /// Checks if a stream contains valid video/audio content using ffprobe with adaptive sampling
    /// </summary>
    /// <param name="stream">The stream to analyze</param>
    /// <param name="filePath">Optional file path for format-specific optimizations</param>
    /// <param name="initialBytes">Initial bytes to try (default 5MB for speed)</param>
    /// <param name="maxBytes">Maximum bytes to try on failure (default 50MB for thoroughness)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the stream contains valid media content, false if corrupt or invalid</returns>
    public static async Task<bool> IsValidMediaStreamAsync(Stream stream, string? filePath = null, int initialBytes = 5 * 1024 * 1024, int maxBytes = 50 * 1024 * 1024, CancellationToken ct = default)
    {
        // Check if this is an MP4 file that might benefit from start+end validation
        var isMp4File = !string.IsNullOrEmpty(filePath) && IsMp4File(filePath);
        
        if (isMp4File && stream.CanSeek && stream.Length > initialBytes * 2)
        {
            Log.Debug("MP4 file detected, using start+end validation strategy for moov atom detection");
            return await ValidateMp4FileAsync(stream, initialBytes, ct);
        }
        
        // For non-MP4 files or non-seekable streams, use the standard approach
        return await ValidateStandardFileAsync(stream, initialBytes, maxBytes, ct);
    }

    /// <summary>
    /// Validates MP4 files by checking both start and end of file for moov atom
    /// </summary>
    private static async Task<bool> ValidateMp4FileAsync(Stream stream, int sampleSize, CancellationToken ct)
    {
        // First attempt: Check beginning of file (optimized MP4s)
        Log.Debug("Checking MP4 file start ({SampleMB}MB) for moov atom", sampleSize / (1024 * 1024));
        stream.Position = 0;
        var isValidStart = await TryValidateMediaStreamAsync(stream, sampleSize, ct);
        
        if (isValidStart)
        {
            Log.Debug("MP4 validation successful with start sample (optimized file)");
            return true;
        }
        
        // Second attempt: Check end of file (unoptimized MP4s with moov at end)
        var endPosition = Math.Max(0, stream.Length - sampleSize);
        Log.Debug("Start validation failed, checking MP4 file end ({SampleMB}MB from position {EndPosition}) for moov atom", 
            sampleSize / (1024 * 1024), endPosition);
        
        stream.Position = endPosition;
        var isValidEnd = await TryValidateMediaStreamAsync(stream, sampleSize, ct);
        
        if (isValidEnd)
        {
            Log.Information("MP4 validation successful with end sample (unoptimized file with late moov atom)");
            return true;
        }
        
        Log.Debug("MP4 validation failed with both start and end samples");
        return false;
    }

    /// <summary>
    /// Standard validation approach for non-MP4 files
    /// </summary>
    private static async Task<bool> ValidateStandardFileAsync(Stream stream, int initialBytes, int maxBytes, CancellationToken ct)
    {
        // First attempt: Try with small sample for speed
        Log.Debug("Attempting media validation with {InitialMB}MB sample", initialBytes / (1024 * 1024));
        var isValid = await TryValidateMediaStreamAsync(stream, initialBytes, ct);
        
        if (isValid)
        {
            Log.Debug("Media validation successful with initial {InitialMB}MB sample", initialBytes / (1024 * 1024));
            return true;
        }
        
        // Second attempt: Try with larger sample for thoroughness
        if (maxBytes > initialBytes)
        {
            Log.Debug("Initial validation failed, retrying with {MaxMB}MB sample", maxBytes / (1024 * 1024));
            
            // Reset stream position if possible
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }
            
            isValid = await TryValidateMediaStreamAsync(stream, maxBytes, ct);
            
            if (isValid)
            {
                Log.Information("Media validation successful with extended {MaxMB}MB sample", maxBytes / (1024 * 1024));
                return true;
            }
        }
        
        Log.Debug("Media validation failed with both {InitialMB}MB and {MaxMB}MB samples", 
            initialBytes / (1024 * 1024), maxBytes / (1024 * 1024));
        return false;
    }

    /// <summary>
    /// Checks if a file is an MP4 container format
    /// </summary>
    private static bool IsMp4File(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".mp4" or ".m4v" or ".mov" or ".3gp" or ".3g2";
    }

    /// <summary>
    /// Attempts to validate media content with a specific sample size
    /// </summary>
    private static async Task<bool> TryValidateMediaStreamAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        try
        {
            // Use ffprobe to check basic media integrity by streaming data
            var ffprobeArgs = "-v error -show_entries format=duration -show_entries stream=codec_type,codec_name -of csv=p=0 -i -";
            
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = ffprobeArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            
            // Stream data to ffprobe's stdin
            var streamTask = StreamDataToProcessAsync(stream, process.StandardInput.BaseStream, maxBytes, ct);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            
            // Wait for streaming to complete, then close stdin
            await streamTask;
            
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException ex) when (ex.Message.Contains("Pipe is broken") || ex.Message.Contains("Broken pipe"))
            {
                Log.Debug("ffprobe stdin pipe already closed (this is normal)");
            }
            
            await process.WaitForExitAsync(ct);
            
            var output = await outputTask;
            var error = await errorTask;

            // Parse and summarize ffprobe output
            var streamSummary = SummarizeStreams(output);
            
            Log.Debug("ffprobe raw output ({SampleMB}MB): Exit code {ExitCode}, Output: '{Output}', Error: '{Error}'", 
                maxBytes / (1024 * 1024), process.ExitCode, output?.Trim(), error?.Trim());

            if (process.ExitCode != 0)
            {
                Log.Debug("ffprobe detected issues ({SampleMB}MB): Exit code {ExitCode}, Error: {Error}", 
                    maxBytes / (1024 * 1024), process.ExitCode, error);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                Log.Debug("ffprobe warnings ({SampleMB}MB): {Error}", maxBytes / (1024 * 1024), error);
            }

            var hasValidOutput = !string.IsNullOrWhiteSpace(output) && (
                output.Contains("video") || 
                output.Contains("audio") || 
                output.Contains("subtitle") ||
                output.Contains(",")
            );
            
            if (!hasValidOutput)
            {
                Log.Debug("No valid media streams detected in {SampleMB}MB sample", maxBytes / (1024 * 1024));
                return false;
            }
            
            Log.Debug("Valid media content detected ({SampleMB}MB): {StreamSummary}", maxBytes / (1024 * 1024), streamSummary);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error running ffprobe on {SampleMB}MB stream sample", maxBytes / (1024 * 1024));
            
            // If ffprobe is not available, log a warning but don't fail the validation
            if (ex is System.ComponentModel.Win32Exception win32Ex && win32Ex.NativeErrorCode == 2)
            {
                Log.Warning("ffprobe is not available - skipping media content validation. Install ffmpeg to enable this feature.");
                return true; // Assume valid if ffprobe is not available
            }
            
            return false;
        }
    }

    private static async Task StreamDataToProcessAsync(Stream source, Stream destination, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[8192]; // 8KB buffer
        var totalRead = 0;
        
        try
        {
            while (totalRead < maxBytes)
            {
                var bytesToRead = Math.Min(buffer.Length, maxBytes - totalRead);
                var bytesRead = await source.ReadAsync(buffer, 0, bytesToRead, ct);
                
                if (bytesRead == 0)
                    break; // End of stream
                    
                try
                {
                    await destination.WriteAsync(buffer, 0, bytesRead, ct);
                    totalRead += bytesRead;
                }
                catch (IOException ex) when (ex.Message.Contains("Broken pipe") || ex.InnerException is SocketException)
                {
                    // ffprobe closed its stdin - this is normal, it has enough data to analyze
                    Log.Debug("ffprobe closed stdin after reading {TotalRead} bytes (this is normal)", totalRead);
                    break;
                }
            }
            
            try
            {
                await destination.FlushAsync(ct);
            }
            catch (IOException ex) when (ex.Message.Contains("Broken pipe") || ex.InnerException is SocketException)
            {
                // Ignore broken pipe on flush - ffprobe already closed
                Log.Debug("ffprobe stdin already closed during flush (this is normal)");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error during streaming to ffprobe (may be normal if ffprobe closed early)");
            // Don't rethrow - this is often normal behavior when ffprobe gets enough data
        }
    }

    private static string SummarizeStreams(string? ffprobeOutput)
    {
        if (string.IsNullOrWhiteSpace(ffprobeOutput))
            return "No streams detected";

        var lines = ffprobeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var videoStreams = new List<string>();
        var audioStreams = new List<string>();
        var subtitleStreams = new List<string>();
        var duration = "";

        foreach (var line in lines)
        {
            var parts = line.Split(',');
            if (parts.Length >= 2)
            {
                var codecType = parts[0].Trim();
                var codecName = parts[1].Trim();

                switch (codecType.ToLowerInvariant())
                {
                    case "video":
                        videoStreams.Add(codecName);
                        break;
                    case "audio":
                        audioStreams.Add(codecName);
                        break;
                    case "subtitle":
                        subtitleStreams.Add(codecName);
                        break;
                }
            }
            else if (parts.Length == 1 && double.TryParse(parts[0], out _))
            {
                duration = parts[0] + "s";
            }
        }

        var summary = new List<string>();
        if (videoStreams.Count > 0)
            summary.Add($"{videoStreams.Count} video ({string.Join(", ", videoStreams.Distinct())})");
        if (audioStreams.Count > 0)
            summary.Add($"{audioStreams.Count} audio ({string.Join(", ", audioStreams.Distinct())})");
        if (subtitleStreams.Count > 0)
            summary.Add($"{subtitleStreams.Count} subtitle");
        if (!string.IsNullOrEmpty(duration))
            summary.Add(duration);

        return summary.Count > 0 ? string.Join(", ", summary) : "Unknown format";
    }
}
