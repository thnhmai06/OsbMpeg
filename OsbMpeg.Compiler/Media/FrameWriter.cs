using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;

namespace OsbMpeg.Media;

/// <summary>Encodes a sequence of rgb24 frames into a video file via ffmpeg's raw-video pipe
/// input. RawVideoPipeSource pulls the first frame eagerly to learn width/height/pixel-format
/// for the input arguments, so `frames` must yield at least one.</summary>
public static class FrameWriter
{
    public static async Task WriteAsync(IEnumerable<IVideoFrame> frames, string outputPath, double fps, CancellationToken ct = default)
    {
        var source = new RawVideoPipeSource(frames) { FrameRate = fps };

        await FFMpegArguments
            .FromPipeInput(source)
            .OutputToFile(outputPath, true, o => o
                .WithFramerate(fps)
                .WithVideoCodec("libx264")
                .WithConstantRateFactor(18)
                .WithSpeedPreset(Speed.Medium)
                .WithCustomArgument("-pix_fmt yuv420p"))
            .ProcessAsynchronously(true);
    }
}
