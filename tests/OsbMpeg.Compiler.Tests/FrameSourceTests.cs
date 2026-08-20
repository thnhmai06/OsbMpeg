using System.Diagnostics;
using OsbMpeg.Compiler.Shared.Media;
using Xunit;

namespace OsbMpeg.Compiler.Tests;

public class FrameSourceTests
{
    [Fact]
    public async Task NonexistentInput_FailsFast_InsteadOfHangingForever()
    {
        // Regression guard: ffmpeg exits immediately on a missing input file, before ever opening
        // its output pipe -- the read side used to block on that pipe connection forever instead
        // of surfacing the failure. Asserting a tight wall-clock bound (not just "throws
        // eventually") is the point: a much looser external cancellation would also make
        // ThrowsAnyAsync pass, for the wrong reason, if the underlying hang regressed.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        async Task ReadAll()
        {
            await foreach (var frame in FrameSource.ReadFramesAsync("nonexistent.mp4",
                               new FrameSourceOptions(64, 64, 30), cts.Token))
                frame.Dispose();
        }

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(ReadAll);
        sw.Stop();

        // Must not be OUR test's own 120s cancellation firing -- that would mean the fix
        // regressed and FrameSource is hanging again. Bounded by FrameSource's own 60s
        // StartupTimeout with slack for process spawn overhead, well under the 120s test-level
        // cancellation that exists only as a last-resort safety net.
        Assert.IsNotType<OperationCanceledException>(ex);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(75),
            $"expected ffmpeg's own failure to surface in well under 75s, took {sw.Elapsed}");
    }
}
