using OsbMpeg.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace OsbMpeg.Encoder;

public sealed record NaiveBaselineStats(long EstimatedStoryboardBytes, int SpriteCount, int CommandCount, int AssetCount);

/// <summary>Estimates the "just dump every frame as its own full-canvas sprite" strawman —
/// the denominator that actually proves the tile codec does something (raw-frame-bytes and
/// source-file comparisons don't). Never fully encodes it: samples frames at a low fixed rate
/// (ffmpeg does the skipping, not a discard-after-decode loop), PNG-measures those, and
/// extrapolates by the real frame count. The plan is explicit that the baseline must not cost
/// more than the real encode.</summary>
public static class NaiveBaseline
{
    private const double SampleFps = 1.0;

    public static async Task<NaiveBaselineStats> EstimateAsync(
        string inputPath, int width, int height, TimeSpan? start, TimeSpan? duration, int realFrameCount, CancellationToken ct = default)
    {
        var opts = new FrameSourceOptions(width, height, SampleFps, start, duration);
        long sampledBytes = 0;
        var sampledCount = 0;

        await foreach (var frame in FrameSource.ReadFramesAsync(inputPath, opts, ct))
        {
            using (frame)
            {
                sampledBytes += MeasurePngBytes(frame.Rgb, width, height);
                sampledCount++;
            }
        }

        var avgBytesPerFrame = sampledCount == 0 ? 0 : sampledBytes / (double)sampledCount;
        var estimatedBytes = (long)(avgBytesPerFrame * realFrameCount);

        return new NaiveBaselineStats(estimatedBytes, realFrameCount, realFrameCount, realFrameCount);
    }

    private static long MeasurePngBytes(ReadOnlySpan<byte> rgb, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgb24>(rgb, width, height);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream, new PngEncoder());
        return stream.Length;
    }
}
