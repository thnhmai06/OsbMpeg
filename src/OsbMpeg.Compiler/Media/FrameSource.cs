using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FFMpegCore;
using FFMpegCore.Pipes;

namespace OsbMpeg.Compiler.Media;

public sealed record FrameSourceOptions(
    int Width,
    int Height,
    double Fps,
    TimeSpan? Start = null,
    TimeSpan? Duration = null,
    string? ExtraInputArgs = null,
    string? ExtraOutputArgs = null);

/// <summary>
///     Streams decoded frames out of ffmpeg without buffering the whole video.
///     ffmpeg writes raw rgb24 into a named pipe (FFMpegCore's OutputToPipe always uses one on
///     Windows, not stdout); we read it in a StreamPipeSink writer delegate and hand completed
///     frames to the caller through a small bounded channel, so ffmpeg naturally blocks (and the
///     OS pipe buffer provides backpressure) whenever the consumer falls behind.
/// </summary>
public static class FrameSource
{
    public static async IAsyncEnumerable<VideoFrame> ReadFramesAsync(
        string inputPath,
        FrameSourceOptions opts,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var frameBytes = opts.Width * opts.Height * 3;
        var channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(8)
            { FullMode = BoundedChannelFullMode.Wait });

        var sink = new StreamPipeSink(async (pipeStream, innerCt) =>
        {
            try
            {
                var readBuffer = new byte[frameBytes];
                var index = 0;
                while (true)
                {
                    var read = 0;
                    while (read < frameBytes)
                    {
                        var n = await pipeStream.ReadAsync(readBuffer.AsMemory(read, frameBytes - read), innerCt);
                        if (n == 0)
                            return; // EOF, possibly mid-frame on a truncated stream — drop the partial tail
                        read += n;
                    }

                    var frame = VideoFrame.Rent(opts.Width, opts.Height, index, index / opts.Fps);
                    readBuffer.AsSpan(0, frameBytes).CopyTo(frame.Buffer);
                    await channel.Writer.WriteAsync(frame, innerCt);
                    index++;
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        var args = FFMpegArguments
            .FromFileInput(inputPath, true, o =>
            {
                if (opts.Start is { } start)
                    o.Seek(start);
                if (opts.ExtraInputArgs is { } inputArgs)
                    o.WithCustomArgument(inputArgs);
            })
            .OutputToPipe(sink, o =>
            {
                // fps filter resamples by timestamp (VFR-safe); "-r" instead forces CFR by
                // duplicating/dropping frames to hit the rate, which is wrong for anything
                // that isn't already exactly that rate.
                var fps = opts.Fps.ToString(CultureInfo.InvariantCulture);
                o.ForceFormat("rawvideo").ForcePixelFormat("rgb24")
                    .WithCustomArgument($"-vf \"fps={fps},scale={opts.Width}:{opts.Height}\"");
                if (opts.Duration is { } duration)
                    o.WithDuration(duration);
                if (opts.ExtraOutputArgs is { } outputArgs)
                    o.WithCustomArgument(outputArgs);
            });

        var processTask = args.ProcessAsynchronously();

        await foreach (var frame in channel.Reader.ReadAllAsync(ct))
            yield return frame;

        await processTask; // surfaces any ffmpeg failure after draining whatever frames did arrive
    }
}