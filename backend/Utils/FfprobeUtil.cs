using System.Diagnostics;
using System.Net.Sockets;
using Serilog;

namespace NzbWebDAV.Utils;

public static class FfprobeUtil
{
    /// <summary>
    /// Checks if a stream contains valid video/audio content using ffprobe
    /// </summary>
    /// <param name="stream">The stream to analyze</param>
    /// <param name="maxBytes">Maximum bytes to stream to ffprobe (default 20MB)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the stream contains valid media content, false if corrupt or invalid</returns>
    public static async Task<bool> IsValidMediaStreamAsync(Stream stream, int maxBytes = 20 * 1024 * 1024, CancellationToken ct = default)
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
            
            Log.Debug("ffprobe raw output: Exit code {ExitCode}, Output: '{Output}', Error: '{Error}'", 
                process.ExitCode, output?.Trim(), error?.Trim());

            if (process.ExitCode != 0)
            {
                Log.Debug("ffprobe detected issues: Exit code {ExitCode}, Error: {Error}", 
                    process.ExitCode, error);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                Log.Debug("ffprobe warnings: {Error}", error);
            }

            var hasValidOutput = !string.IsNullOrWhiteSpace(output) && (
                output.Contains("video") || 
                output.Contains("audio") || 
                output.Contains("subtitle") ||
                output.Contains(",")
            );
            
            if (!hasValidOutput)
            {
                Log.Debug("No valid media streams detected in content");
                return false;
            }
            
            Log.Debug("Valid media content detected: {StreamSummary}", streamSummary);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error running ffprobe on stream");
            
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
