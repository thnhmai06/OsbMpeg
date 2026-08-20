using FFMpegCore;

namespace OsbMpeg.Compiler.Shared.Media;

public sealed record MediaInfo(int Width, int Height, double SourceFps, TimeSpan Duration, string CodecName);

/// <summary>
///     Thin wrapper over FFProbe. Uses AvgFrameRate (ffprobe's avg_frame_rate, total
///     frames / duration) rather than FrameRate/AverageFrameRate — FFMpegCore exposes three
///     near-identically named properties with no documentation distinguishing them; AvgFrameRate
///     is the one that matches what "--keep-source" needs (the rate frames actually arrive at),
///     as opposed to a stream's nominal/base rate.
/// </summary>
public static class MediaProbe
{
    public static async Task<MediaInfo> AnalyseAsync(string path, CancellationToken ct = default)
    {
        var analysis = await FFProbe.AnalyseAsync(path, cancellationToken: ct);
        var video = analysis.PrimaryVideoStream
                    ?? throw new InvalidOperationException($"{path} has no video stream.");

        return new MediaInfo(video.Width, video.Height, video.AvgFrameRate, analysis.Duration, video.CodecName);
    }
}