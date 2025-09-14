using FFMpegCore;
using Serilog;

namespace NzbWebDAV.Utils;

public static class FfprobeUtil
{
    /// <summary>
    /// Checks if a stream contains valid video/audio content using FFMpegCore
    /// </summary>
    /// <param name="stream">The stream to analyze</param>
    /// <param name="filePath">Optional file path for logging purposes</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the stream contains valid media content, false if corrupt or invalid</returns>
    public static async Task<bool> IsValidMediaStreamAsync(Stream stream, string? filePath = null, CancellationToken ct = default)
    {
        try
        {
            Log.Debug("Analyzing media stream using FFMpegCore for {FilePath}", filePath ?? "unknown file");
            
            // Use FFMpegCore to analyze the entire stream
            var mediaInfo = await FFProbe.AnalyseAsync(stream, cancellationToken: ct);
            
            // Check if we have any video or audio streams
            var hasVideo = mediaInfo.VideoStreams.Any();
            var hasAudio = mediaInfo.AudioStreams.Any();
            var hasValidContent = hasVideo || hasAudio;
            
            if (hasValidContent)
            {
                var streamSummary = CreateStreamSummary(mediaInfo);
                Log.Information("Media analysis PASSED for {FilePath}: {StreamSummary}", 
                    filePath ?? "stream", streamSummary);
                return true;
            }
            else
            {
                Log.Warning("Media analysis FAILED for {FilePath}: No video or audio streams found", 
                    filePath ?? "stream");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error analyzing media stream for {FilePath}", filePath ?? "stream");
            return false;
        }
    }

    /// <summary>
    /// Creates a human-readable summary of media streams
    /// </summary>
    private static string CreateStreamSummary(IMediaAnalysis mediaInfo)
    {
        var summary = new List<string>();
        
        if (mediaInfo.VideoStreams.Any())
        {
            var videoCodecs = mediaInfo.VideoStreams.Select(v => v.CodecName).Distinct();
            summary.Add($"{mediaInfo.VideoStreams.Count()} video ({string.Join(", ", videoCodecs)})");
        }
        
        if (mediaInfo.AudioStreams.Any())
        {
            var audioCodecs = mediaInfo.AudioStreams.Select(a => a.CodecName).Distinct();
            summary.Add($"{mediaInfo.AudioStreams.Count()} audio ({string.Join(", ", audioCodecs)})");
        }
        
        if (mediaInfo.SubtitleStreams.Any())
        {
            summary.Add($"{mediaInfo.SubtitleStreams.Count()} subtitle");
        }
        
        if (mediaInfo.Duration != TimeSpan.Zero)
        {
            summary.Add($"{mediaInfo.Duration.TotalSeconds:F1}s");
        }
        
        return summary.Count > 0 ? string.Join(", ", summary) : "Unknown format";
    }
}