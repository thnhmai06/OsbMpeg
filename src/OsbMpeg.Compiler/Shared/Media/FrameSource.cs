using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FFMpegCore;
using FFMpegCore.Pipes;

namespace OsbMpeg.Compiler.Shared.Media;

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
///     StartupTimeout guards one specific failure mode: if ffmpeg exits before ever opening the
///     pipe at all (e.g. the input file doesn't exist — verified with a real ffmpeg run that it
///     exits cleanly in ~1.3s in that case, it is not the ffmpeg process itself hanging), the .NET
///     side's NamedPipeServerStream is left waiting for a connection that will never come — no
///     EOF, no exception, it just hangs forever, and nothing about the ffmpeg process having
///     already exited unblocks it on its own. Three earlier attempts at this fix, each measured
///     wrong rather than assumed: (1) cancelling once FFMpegArguments.ProcessAsynchronously()'s
///     own task completed — that task doesn't return until the sink's pipe I/O finishes, so it's
///     blocked on the exact same stuck wait and never fires; (2) bounding only the sink callback's
///     first ReadAsync — the connection handshake happens *before* the sink delegate is ever
///     invoked when the client never connects, so no code inside the sink ever runs to enforce a
///     bound; (3) FFMpegArgumentProcessor.CancellableThrough(token) to kill the ffmpeg process —
///     pointless once the process has already exited on its own, which is exactly this case; it
///     doesn't touch the .NET-side pipe-server wait at all. What actually works: bound only *our
///     own* channel wait (ChannelReader.WaitToReadAsync takes a token per call, entirely within
///     this method's control, independent of whatever FFMpegCore is doing internally) for the
///     first item specifically. Once real data has flowed once, decode pacing is normal and
///     unbounded, same as before. If the timeout does fire, the sink's own background read stays
///     orphaned, blocked forever on that pipe connection — accepted for this narrow, essentially
///     always-a-caller-bug case (a nonexistent/unreadable input) in exchange for the caller never
///     hanging, rather than chasing a fully clean teardown through a third-party black box.
/// </summary>
public static class FrameSource
{
    // 60s, not 20s: ParameterTuner probes 3-4 candidate parameter values concurrently within
    // one axis (Task.WhenAll), so several ffmpeg startups can be competing for CPU/disk at
    // once. A genuinely broken input still exits in ~1.3s (see above), so this only slows down
    // that error case -- it doesn't mask it.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

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

        using (var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            startupCts.CancelAfter(StartupTimeout);
            bool hasData;
            try
            {
                hasData = await channel.Reader.WaitToReadAsync(startupCts.Token);
            }
            catch (OperationCanceledException) when (startupCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"ffmpeg produced no output within {StartupTimeout} decoding \"{inputPath}\" — it likely " +
                    "failed before ever opening its output pipe (check the input path and ffmpeg's own stderr).");
            }

            if (!hasData)
                await processTask; // channel completed with nothing — surface ffmpeg's real failure, if any
        }

        await foreach (var frame in channel.Reader.ReadAllAsync(ct))
            yield return frame;

        await processTask; // surfaces any ffmpeg failure after draining whatever frames did arrive
    }
}
