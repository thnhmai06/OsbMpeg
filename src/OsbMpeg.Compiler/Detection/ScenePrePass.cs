using System.Diagnostics;
using OsbMpeg.Compiler.Shared.Analysis;
using OsbMpeg.Compiler.Shared.Media;

namespace OsbMpeg.Compiler.Detection;

/// <summary>Cut timestamps inside the scanned window (absolute, file-relative). StartMs/EndMs
///     always equal the requested window exactly — nothing extends past it (see the class doc
///     comment for why an earlier margin-fetch mechanism here was removed).</summary>
public readonly record struct ScenePrePassResult(double StartMs, double EndMs, IReadOnlyList<double> Cuts);

/// <summary>
///     Finds hard-cut scene boundaries inside a requested window, cheaply. A frame where almost
///     every tile position closes its run simultaneously is a hard cut, under any TileSize/
///     HashQuantLevels/TileTolerance — the closed-run fraction spikes to ~1.0 regardless of which
///     params are active, so detection needs no reference combo and no rolling window (an earlier,
///     rejected design compared cost-rate drift against the active combo's own tuning-time rate —
///     that's a lagging signal that smears the one frame a cut actually is; see docs/research.md).
///     Decodes with a fixed baseline combo (64/32/8/0, canonical snapshot) and only runs
///     TileRunTracker.Advance — no QuadtreeMerger, AnimationDetector, AssetStore, or PNG writes at
///     all, which is why this is far cheaper than a real encode.
///     Window-scoped, not whole-file: an earlier version always decoded the entire source file to
///     find every cut it contains, even though a .osbv compile only ever needs the scenes
///     overlapping its own requested window — for a long file with many natural cuts this paid the
///     cost of scenes nowhere near what was actually being tuned/encoded. ScanAsync decodes only
///     <paramref name="windowStartMs" />/<paramref name="windowEndMs" />, nothing past it.
///     No margin-fetch past the window edges (an earlier version had one, padding a short scene out
///     to whatever sample size ParameterTuner needed): removed once ParameterTuner's own design
///     stopped needing it — a scene no longer than ParameterTuner.RequiredSampleMs is now tuned
///     using its own full span directly (see ParameterTuner.BuildSampleWindows), not a
///     margin-padded stand-in for a bigger sample. Detection only ever needs to answer one
///     question now: is there a cut *inside* this window (if so, split at it) — VideoCompiler's own
///     Clip()+skip logic already intersects any reported scene bound back down to the real
///     requested encode window regardless, so there was never a reason for detection itself to look
///     past its own window's edges.
///     DetectCuts is the actual decision logic, split out as a pure function over
///     (timestamp, closed-run count) pairs so it's testable without real video decode — same split
///     ParameterTuner already uses between ProbeAsync (I/O) and TuneCoreAsync (decision).
/// </summary>
public static class ScenePrePass
{
    private const int BaselineTileSize = 64;
    private const int BaselineHashQuantLevels = 32;
    private const int BaselineTileTolerance = 8;

    /// <summary>
    ///     Fraction of tile positions that must close in the same frame to count as a cut.
    ///     0.8 (the original guess, same order of magnitude as AnimationDetector's existing
    ///     minAnimationUniqueness=0.8 heuristic) missed a real concat-boundary hard cut measured
    ///     at 0.721 on a real fixture, while an ordinary mid-scene high-motion frame measured 0.662
    ///     — 0.7 is the value that sits strictly between those two real measurements (see
    ///     docs/research.md for the full calibration data).
    /// </summary>
    internal const double CutThreshold = 0.7;

    /// <summary>
    ///     Frames above CutThreshold within this many ms of each other are one physical event
    ///     (a hard cut, or a sustained turbulent/high-motion stretch), not several — collapse them
    ///     to one candidate at the event's own start. Small relative to frame spacing (30fps ≈
    ///     33ms/frame) so it merges brief 1-2 frame dips *within* one event without merging two
    ///     genuinely separate events that happen to land close together (measured: a real fixture
    ///     has two distinct hard cuts only ~1.8s apart, well under the old design's fixed 2000ms
    ///     "time since last accepted cut" window, which swallowed the second one entirely).
    /// </summary>
    internal const double BurstGapMs = 300;

    /// <summary>
    ///     Minimum resulting scene length — guards only against a degenerate near-zero-length
    ///     scene (two burst-collapsed candidates landing within a handful of frames of each
    ///     other), nothing more. A first design tried to also suppress *any* candidate within 2s of
    ///     the previous one, on the theory that close-together events are noise from one physical
    ///     cut — real fixture data disproved that (see docs/research.md): a genuine hard cut
    ///     (bad_apple→birdbrain) sat only ~1.6s before a real, separate sustained high-motion
    ///     stretch *inside* birdbrain's own footage, and merging them lost the real cut. Per this
    ///     feature's own definition of a scene (the longest span one tuned combo stays a good fit),
    ///     a genuinely distinct high-motion stretch earning its own scene is correct, not noise to
    ///     suppress — the extra tuning-pass cost of a short-but-real scene is an accepted,
    ///     already-documented tradeoff of this whole feature, not a defect to engineer away here.
    /// </summary>
    internal const double MinSceneMs = 1000;

    public static async Task<ScenePrePassResult> ScanAsync(string inputPath, int width, int height, double fps,
        double windowStartMs, double windowEndMs, string? hwAccel, Action<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var grid = new TileGrid(width, height, BaselineTileSize);
        var tracker = new TileRunTracker(grid, BaselineHashQuantLevels, true, BaselineTileTolerance);
        var frameOpts = new FrameSourceOptions(width, height, fps,
            TimeSpan.FromMilliseconds(windowStartMs), TimeSpan.FromMilliseconds(windowEndMs - windowStartMs),
            hwAccel is { } hw ? $"-hwaccel {hw}" : null);

        var samples = new List<(double Ms, int ClosedCount)>();
        await foreach (var frame in FrameSource.ReadFramesAsync(inputPath, frameOpts, ct))
            using (frame)
            {
                // VideoFrame.Pts is always 0-based within its own decode's output stream regardless
                // of the -ss seek above (confirmed at FrameSource.cs) -- add windowStartMs back to
                // land on an absolute, file-relative timestamp.
                var ms = windowStartMs + frame.Pts * 1000.0;
                var closed = tracker.Advance(frame, ms);
                samples.Add((ms, closed.Count));
            }

        var cuts = DetectCuts(samples, grid.TileCount);
        log?.Invoke($"scene prepass {Path.GetFileName(inputPath)}: {sw.ElapsedMilliseconds}ms, " +
                    $"window=[{windowStartMs:F0},{windowEndMs:F0}), {cuts.Count} internal cut(s)");
        return new ScenePrePassResult(windowStartMs, windowEndMs, cuts);
    }

    internal static List<double> DetectCuts(IEnumerable<(double Ms, int ClosedCount)> frames, int tileCount)
    {
        if (tileCount == 0) return [];

        // Pass 1: collapse each contiguous burst of above-threshold frames (allowing gaps up to
        // BurstGapMs so a brief 1-2 frame dip mid-event doesn't split it) into one candidate at
        // the burst's own start time.
        var candidates = new List<double>();
        var burstStartMs = double.NaN;
        var lastHighMs = double.NegativeInfinity;

        foreach (var (ms, closedCount) in frames)
        {
            var fraction = closedCount / (double)tileCount;
            if (fraction < CutThreshold) continue;

            if (double.IsNaN(burstStartMs) || ms - lastHighMs > BurstGapMs)
            {
                if (!double.IsNaN(burstStartMs)) candidates.Add(burstStartMs);
                burstStartMs = ms;
            }

            lastHighMs = ms;
        }

        if (!double.IsNaN(burstStartMs)) candidates.Add(burstStartMs);

        // Pass 2: drop a candidate only if it would produce a degenerate near-zero-length scene
        // right after the previous one (see MinSceneMs's own doc comment for why this is
        // deliberately not a general "suppress anything within N seconds" merge).
        var cuts = new List<double>();
        var lastAcceptedMs = double.NegativeInfinity;
        foreach (var c in candidates)
        {
            if (c - lastAcceptedMs < MinSceneMs) continue;
            cuts.Add(c);
            lastAcceptedMs = c;
        }

        return cuts;
    }
}
