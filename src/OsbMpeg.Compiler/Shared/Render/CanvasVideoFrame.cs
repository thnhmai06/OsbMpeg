using FFMpegCore.Pipes;

namespace OsbMpeg.Compiler.Shared.Render;

/// <summary>Adapts a rendered Canvas to FFMpegCore's raw-video pipe input contract.</summary>
public sealed class CanvasVideoFrame(Canvas canvas) : IVideoFrame
{
    public int Width => canvas.Width;
    public int Height => canvas.Height;
    public string Format => "rgb24";

    public void Serialize(Stream pipe)
    {
        pipe.Write(canvas.Rgb);
    }

    public Task SerializeAsync(Stream pipe, CancellationToken token)
    {
        return pipe.WriteAsync(canvas.Rgb, token).AsTask();
    }
}