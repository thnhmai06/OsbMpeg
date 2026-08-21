using OsbMpeg.Compiler.Encode;
using OsbMpeg.Compiler.Shared.Evaluation;
using OsbMpeg.Compiler.Shared.Media;
using OsbMpeg.Compiler.Shared.Render;
using OsbMpeg.Parsers.Ir;

namespace OsbMpeg.Compiler.Tuning;

public sealed record TunedParameters(int TileSize, int HashQuantLevels, int TileTolerance, int Colors);

/// <summary>
///     Picks TileSize/HashQuantLevels/TileTolerance/Colors per video source instead of
///     the hardcoded defaults `VideoCompiler.LoopOptions` used to carry. Always-on, no CLI flag —
///     "auto-tune" means "as cheap as possible without losing what today's hardcoded combo already
///     delivers," not quality-maximization for its own sake.
///     Target is self-calibrating, not an absolute PSNR constant: probe once at today's own
///     defaults (TileSize=64, HashQuantLevels=32, TileTolerance=8, Colors=0) on the train samples
///     (see below), use that PSNR (minus <see cref="TargetSlackDb" />) as the floor every candidate
///     must clear on *both* train and eval. A hard zero-slack floor would make every
///     lossy-but-cheaper candidate (any Colors quantization, any TileTolerance above the baseline's
///     own 8) score below the baseline by construction and never win — the slack is what lets the
///     search actually find something cheaper instead of always falling back to the baseline it
///     started from.
///     Train/eval split, not one sample: a single probe window can overfit a candidate to whatever
///     that one clip happens to look like — a combo that wins on the sampled clip might cost far
///     more (or look far worse) on the rest of the scene. Each candidate is probed against 3 short
///     train windows spread across the scene (whose combined PSNR/cost drives the search, same
///     role the old single sample played) *and* 1 held-out eval window elsewhere in the scene the
///     candidate never sees during selection — a candidate only counts as "passing the floor" if
///     both its train PSNR and its eval PSNR clear it (<see cref="Select" />), so a combo that
///     looks great on train but craters on unseen material gets rejected the same as one that never
///     met the floor at all. See <see cref="BuildSampleWindows" /> for exactly how the 4 windows are
///     placed.
///     Coordinate descent, not a 4D grid: a full grid at 3-4 candidates per axis is 81-625 probes
///     per source, too expensive (each probe re-runs the tile-grid encoder + software renderer over
///     a real decode). One pass, axes ordered biggest-lever-first: TileSize (changes every tile
///     boundary) → Colors (orthogonal PNG palette, doesn't change which tiles get cut) →
///     HashQuantLevels (run-continuation hash proposals) → TileTolerance (confirm-check on top of
///     the hash, this session already measured its cost as highly content-dependent — tuned last,
///     once the other 3 are fixed, as the fine adjustment).
///     Probe is cheaper per-iteration than bench's own reporting path (BenchCommand.ExecuteAsync):
///     no OsbWriter/OsbReader text round-trip (nothing reads the search's output back) and no
///     recon-video round-trip (SoftwareStoryboardRenderer's Canvas.Rgb is already a packed Rgb24
///     buffer, directly comparable via Metrics.Psnr — no FrameWriter/ffmpeg-reencode/ffmpeg-redecode
///     needed on the reconstructed side). Source frames for the comparison are captured via
///     TileEncodeLoop.RunAsync's onFrame hook as it decodes them for its own purposes, instead of
///     decoding the same short window a second time afterward — a real, measured cost this design
///     initially missed. Each candidate writes its throwaway PNGs to an in-memory AssetStore (see
///     AssetStore.cs's own doc comment on `inMemory`) — never touches the real, persistent,
///     content-addressed store: a different TileSize hashes completely differently anyway, so
///     there's no reuse to be had during search, only pollution to avoid.
/// </summary>
public static class ParameterTuner
{
    private const double TargetSlackDb = 1.0;
    private const double BytesPerCommandEstimate = 100; // fish_spin_test: 44.63KB / 445 commands ≈ 100.3, this session

    /// <summary>
    ///     Total width of the one local sample block <see cref="BuildSampleWindows" /> carves into 4
    ///     contiguous train/eval chunks — also used by Detection (ScenePrePass's margin-fetch, via
    ///     VideoCompiler) as the "how much material does one scene's tuning sample need" input, so a
    ///     scene too short for this gets padded by a margin fetch before TuneAsync ever runs (see
    ///     ScenePrePass.cs). Up from the single-sample design's 1500ms — the eval chunk is a real
    ///     added cost, not free visibility.
    ///     Deliberately one contiguous local block, not spread across the whole scene: an earlier
    ///     version placed the 4 samples in 4 quarters spanning the *entire* scene, which meant the
    ///     baseline probe (whichever content those quarters happened to average over) could measure
    ///     meaningfully higher or lower quality than the old single-anchored-sample design ever did —
    ///     shifting the whole floor (baseline PSNR - TargetSlackDb) up or down by several dB purely
    ///     from sampling different content, not from any candidate actually being better or worse.
    ///     Measured on a real fixture: this produced baseline PSNR 3+dB higher than a same-content
    ///     single-sample run, which pushed the floor high enough that almost every cheaper candidate
    ///     failed it — total output bytes +18.9% and tuning ~2x slower on the same real scene,
    ///     despite the mechanism "working as designed." Keeping train+eval within one local
    ///     neighborhood (same place the old single sample was drawn from) keeps the floor comparable
    ///     to what it always measured, while still giving the overfitting gate real held-out material
    ///     to check against.
    /// </summary>
    internal const double RequiredSampleMs = 3000;

    private static readonly int[] TileSizeCandidates = [64, 128, 256];
    private static readonly int[] ColorsCandidates = [0, 32, 16];
    private static readonly int[] HashQuantCandidates = [32, 16, 8];
    private static readonly int[] ToleranceCandidates = [0, 4, 8, 16];

    /// <summary>
    ///     Tunes one scene/segment of a video — <paramref name="segmentStartMs" />/
    ///     <paramref name="segmentEndMs" /> are absolute, file-relative timestamps (from
    ///     VideoCompiler's own scene list, anchored to the whole file), not the calling .osbv
    ///     project's own window. A video with no detected scene cuts is exactly one segment spanning
    ///     [0, duration) with <paramref name="isFirstSegment" />=true — the general form this always
    ///     was, not a parallel path.
    /// </summary>
    public static async Task<TunedParameters> TuneAsync(string inputPath, MediaInfo info, double fps,
        double segmentStartMs, double segmentEndMs, bool isFirstSegment, string? hwAccel, Action<string>? log,
        CancellationToken ct, Action<ProbeWindowTimes>? onWindowTimed = null, bool shareDecode = true,
        int maxConcurrency = 4)
    {
        var (trainWindows, evalWindow) =
            BuildSampleWindows(segmentStartMs, segmentEndMs - segmentStartMs, isFirstSegment);

        // Decode the fixed sample windows exactly once, up front, instead of once per candidate:
        // the windows depend only on the scene (BuildSampleWindows), never on the parameter tuple,
        // so every probe after the first would re-decode identical bytes. One ffmpeg pass per window
        // replaces 10 (the TileSize/Colors/HashQuant/Tolerance batch sizes), and every probe
        // afterward runs the full encode/PSNR pipeline from the shared buffers. The frames are
        // held for the whole search (the 4 windows are at most ~3s of content — bounded, not
        // proportional to scene size); see DecodeSampleWindowsAsync/ProbeAsync for how they're fed in.
        // When shareDecode=false (tune-bench --no-shared A/B mode), sharedFrames stays null and each
        // probe decodes its own window via ffmpeg (the pre-optimization path).
        var sharedFrames = shareDecode
            ? await DecodeSampleWindowsAsync(inputPath, info, fps,
                [.. trainWindows.Append(evalWindow).Distinct()], hwAccel, ct)
            : null;

        // Global semaphore to bound total concurrent CPU probe work (track/merge/emit/render/psnr)
        // across the whole search — honors tune-bench's --max-concurrency flag.
        using var cpuSemaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        return await TuneCoreAsync(
            (tileSize, hashQuantLevels, tileTolerance, colors) =>
                ProbeTrainEvalAsync(inputPath, info, fps, trainWindows, evalWindow, tileSize, hashQuantLevels,
                    tileTolerance, colors, sharedFrames, ct, onWindowTimed, cpuSemaphore),
            Path.GetFileName(inputPath), log);
    }

    /// <summary>
    ///     Captures the raw packed-Rgb24 frames of each sample window in one ffmpeg pass per
    ///     window (Start is absolute file time; Pts stays 0-based within each window like the
    ///     encode path expects). Dict keyed by <c>(Start, DurationMs)</c> — exact double equality
    ///     with the values ProbeTrainEvalAsync looks up, all derived from the same BuildSampleWindows.
    /// </summary>
    private static async Task<Dictionary<(double Start, double DurationMs), List<byte[]>>>
        DecodeSampleWindowsAsync(string inputPath, MediaInfo info, double fps,
            (double Start, double DurationMs)[] windows, string? hwAccel, CancellationToken ct)
    {
        var decoded = new Dictionary<(double Start, double DurationMs), List<byte[]>>();
        foreach (var w in windows)
        {
            var frameOpts = new FrameSourceOptions(info.Width, info.Height, fps,
                TimeSpan.FromMilliseconds(w.Start), TimeSpan.FromMilliseconds(w.DurationMs),
                hwAccel != null ? $"-hwaccel {hwAccel}" : null);

            var frames = new List<byte[]>();
            await foreach (var frame in FrameSource.ReadFramesAsync(inputPath, frameOpts, ct))
                using (frame)
                    frames.Add([.. frame.Rgb]);
            decoded[w] = frames;
        }

        return decoded;
    }

    /// <summary>
    ///     Two cases. A scene no longer than <see cref="RequiredSampleMs" /> *is* the whole
    ///     deliverable, not a sample standing in for something bigger — there's no "unseen data" to
    ///     hold out when 100% of what exists already gets used, so overfitting to it isn't a risk to
    ///     guard against, it's the goal (a combo tuned as tightly as possible to exactly this
    ///     content). That scene's entire span becomes the one and only window, returned as both the
    ///     single train window and the eval window — <see cref="ProbeTrainEvalAsync" /> recognizes
    ///     the identical window and skips probing it twice, and <see cref="Select" />'s
    ///     `train.Psnr >= floor &amp;&amp; eval.Psnr >= floor` gate degrades to just the train check
    ///     for free (same value on both sides) without any special-casing in the gate itself.
    ///     Longer scenes place one local <see cref="RequiredSampleMs" />-wide block — positioned
    ///     exactly like the old single-sample design's own window (first segment overall: centered
    ///     within the segment, to avoid an atypical intro/title-card; every later segment: anchored
    ///     at the segment's own start, to fold that segment's one-time full-canvas re-emit cost into
    ///     what gets measured) — then splits that block into 4 equal, contiguous chunks: chunks 0, 1,
    ///     3 become the train windows (their combined PSNR/cost drives the search), chunk 2 becomes
    ///     the held-out eval window. All 4 chunks come from the same local neighborhood deliberately
    ///     (see <see cref="RequiredSampleMs" />'s own doc comment for the real regression measured
    ///     when they were spread across the whole scene instead).
    ///     Pure function, no decode — testable with synthetic segment bounds.
    /// </summary>
    internal static ((double Start, double DurationMs)[] Train, (double Start, double DurationMs) Eval)
        BuildSampleWindows(double segmentStartMs, double segmentDurationMs, bool isFirstSegment)
    {
        if (segmentDurationMs <= RequiredSampleMs)
        {
            var whole = (segmentStartMs, segmentDurationMs);
            return ([whole], whole);
        }

        var blockStart = isFirstSegment
            ? segmentStartMs + Math.Max(0, (segmentDurationMs - RequiredSampleMs) / 2.0)
            : segmentStartMs;

        var chunkMs = RequiredSampleMs / 4.0;

        var train = new[] { (ChunkStart(0), chunkMs), (ChunkStart(1), chunkMs), (ChunkStart(3), chunkMs) };
        var eval = (ChunkStart(2), chunkMs);
        return (train, eval);

        double ChunkStart(int i) => blockStart + chunkMs * i;
    }

    /// <summary>
    ///     The search itself, independent of ffmpeg/rendering — <paramref name="probe" /> is
    ///     injected (same pattern as <c>VideoSourcePlanner.PlanAsync</c>'s injected probe delegate)
    ///     so the coordinate-descent decision logic (slack floor, train/eval gate, cost comparison,
    ///     fallback-to-best, probe budget) is testable with synthetic <see cref="ProbeResult" />
    ///     pairs, no real video decode needed.
    /// </summary>
    internal static async Task<TunedParameters> TuneCoreAsync(
        Func<int, int, int, int, Task<(ProbeResult Train, ProbeResult Eval)>> probe, string label,
        Action<string>? log)
    {
        var hashQuantLevels = 32;
        var tileTolerance = 8;
        var colors = 0;

        // TileSize=64 with the other 3 params at their defaults IS today's baseline combo, and
        // it's already one of TileSizeCandidates — probe the whole axis in one concurrent batch
        // and pull the baseline out of whichever result comes back for value 64, instead of a
        // separate blocking probe that used to run in front of this axis.
        var tileSizeResults = await Task.WhenAll(
            TileSizeCandidates.Select(v => probe(v, hashQuantLevels, tileTolerance, colors)));
        var baseline = tileSizeResults[Array.IndexOf(TileSizeCandidates, 64)];
        var floor = baseline.Train.Psnr - TargetSlackDb;
        log?.Invoke($"tuning {label}: baseline train PSNR={baseline.Train.Psnr:F2}dB " +
                    $"eval PSNR={baseline.Eval.Psnr:F2}dB, floor={floor:F2}dB");

        var (tileSize, tileSizeWin, _) =
            Select(TileSizeCandidates, tileSizeResults, floor, 64, log, "tileSize");

        // Every later axis's "unchanged" candidate — the value already fixed on entry — probes
        // the exact same tuple the previous axis's own winning candidate already resolved (same
        // params, nothing new varies). Carry that pair forward as a seed so BestAsync skips
        // re-probing it instead of paying for the same deterministic combo (now 4 real decodes,
        // not 1) twice.
        var (colorsChosen, colorsWin, _) = await BestAsync(ColorsCandidates,
            v => probe(tileSize, hashQuantLevels, tileTolerance, v), floor, colors, tileSizeWin, log, nameof(colors));

        var (hashQuantChosen, hashQuantWin, _) = await BestAsync(HashQuantCandidates,
            v => probe(tileSize, v, tileTolerance, colorsChosen), floor, hashQuantLevels, colorsWin, log,
            nameof(hashQuantLevels));

        var (toleranceChosen, _, metFloor) = await BestAsync(ToleranceCandidates,
            v => probe(tileSize, hashQuantChosen, v, colorsChosen), floor, tileTolerance, hashQuantWin, log,
            nameof(tileTolerance));

        if (!metFloor)
        {
            log?.Invoke(
                $"tuning {label}: no combo met the floor ({floor:F2}dB) on both train and eval — falling back to baseline defaults");
            return new TunedParameters(64, 32, 8, 0);
        }

        log?.Invoke(
            $"tuning {label}: chosen TileSize={tileSize} HashQuantLevels={hashQuantChosen} " +
            $"TileTolerance={toleranceChosen} Colors={colorsChosen} (baseline train PSNR={baseline.Train.Psnr:F2}dB)");
        return new TunedParameters(tileSize, hashQuantChosen, toleranceChosen, colorsChosen);
    }

    /// <summary>
    ///     Probes every candidate except <paramref name="current" /> — that one's result is
    ///     already known (<paramref name="seed" />, the previous axis's own winning probe pair,
    ///     which necessarily used the exact same tuple this axis's unchanged candidate would probe
    ///     again) — then hands the assembled results to <see cref="Select" />.
    ///     Candidates within one axis are independent (each writes to its own throwaway in-memory
    ///     store, no shared mutable state), so they run concurrently via Task.WhenAll — the 4
    ///     sub-probes (3 train + 1 eval) *within* one candidate run sequentially instead (see
    ///     ProbeTrainEvalAsync), deliberately not fanned out further: candidates-within-an-axis
    ///     concurrency is what this session already measured as the safe amount of parallel ffmpeg
    ///     load (see docs/research.md's per-scene tuning entries) — adding a second layer of
    ///     concurrency here would multiply concurrent decodes past that, not add real throughput.
    /// </summary>
    private static async Task<(int Value, (ProbeResult Train, ProbeResult Eval) Result, bool MetFloor)> BestAsync(
        int[] candidates, Func<int, Task<(ProbeResult Train, ProbeResult Eval)>> probe, double floor, int current,
        (ProbeResult Train, ProbeResult Eval) seed, Action<string>? log, string axisName)
    {
        var seedIndex = Array.IndexOf(candidates, current);
        var tasks = new Task<(ProbeResult Train, ProbeResult Eval)>?[candidates.Length];
        for (var i = 0; i < candidates.Length; i++)
            if (i != seedIndex)
                tasks[i] = probe(candidates[i]);

        await Task.WhenAll(tasks.Where(t => t is not null)!);

        var results = new (ProbeResult Train, ProbeResult Eval)[candidates.Length];
        for (var i = 0; i < candidates.Length; i++)
            results[i] = i == seedIndex ? seed : tasks[i]!.Result;

        return Select(candidates, results, floor, current, log, axisName);
    }

    /// <summary>
    ///     Picks the candidate with the smallest train cost among those where *both* train and eval
    ///     PSNR meet <paramref name="floor" /> — a candidate that only clears the floor on train is
    ///     treated as failing, the same as one that never cleared it on either (this is the actual
    ///     overfitting guard: a combo the search would otherwise love because it looks great on the
    ///     3 windows it got tuned against, but whose PSNR collapses on the 4th window it never saw,
    ///     doesn't get to win just because train alone looked good). If none pass, falls back to the
    ///     highest-train-PSNR candidate (a later axis may still recover the target) and reports
    ///     <c>MetFloor: false</c>.
    /// </summary>
    private static (int Value, (ProbeResult Train, ProbeResult Eval) Result, bool MetFloor) Select(
        int[] candidates, (ProbeResult Train, ProbeResult Eval)[] results, double floor, int current,
        Action<string>? log, string axisName)
    {
        (ProbeResult Train, ProbeResult Eval)? bestPassing = null;
        var bestPassingValue = current;
        (ProbeResult Train, ProbeResult Eval)? bestOverall = null;
        var bestOverallValue = current;

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            var (train, eval) = results[i];
            log?.Invoke($"  {axisName}={candidate}: trainPSNR={train.Psnr:F2}dB evalPSNR={eval.Psnr:F2}dB " +
                        $"cost={train.Cost:F0} took={train.ElapsedMs + eval.ElapsedMs}ms " +
                        $"(decode={train.DecodeMs + eval.DecodeMs}ms render+psnr={train.RenderMs + eval.RenderMs}ms " +
                        $"objects={train.ObjectCount + eval.ObjectCount})");

            if (bestOverall is null || train.Psnr > bestOverall.Value.Train.Psnr)
            {
                bestOverall = (train, eval);
                bestOverallValue = candidate;
            }

            var passesBoth = train.Psnr >= floor && eval.Psnr >= floor;
            if (passesBoth && (bestPassing is null || train.Cost < bestPassing.Value.Train.Cost))
            {
                bestPassing = (train, eval);
                bestPassingValue = candidate;
            }
        }

        return bestPassing is not null
            ? (bestPassingValue, bestPassing.Value, true)
            : (bestOverallValue, bestOverall!.Value, false);
    }

    internal readonly record struct ProbeResult(double Psnr, long AssetBytes, int CommandCount, long ElapsedMs = 0,
        long DecodeMs = 0, long RenderMs = 0, int ObjectCount = 0)
    {
        public double Cost => AssetBytes + CommandCount * BytesPerCommandEstimate;
    }

    /// <summary>
    ///     Runs the 3 train probes plus the 1 eval probe for one candidate tuple, sequentially (see
    ///     BestAsync's own doc comment on why not concurrently), and folds the 3 train results into
    ///     one aggregate (<see cref="ProbeResult.Psnr" /> averaged — representative quality across
    ///     the 3 windows; bytes/commands summed — representative of what this candidate would
    ///     actually cost applied across material like this).
    /// </summary>
    private static async Task<(ProbeResult Train, ProbeResult Eval)> ProbeTrainEvalAsync(string inputPath,
        MediaInfo info, double fps, (double Start, double DurationMs)[] trainWindows,
        (double Start, double DurationMs) evalWindow, int tileSize, int hashQuantLevels, int tileTolerance,
        int colors, IReadOnlyDictionary<(double Start, double DurationMs), List<byte[]>>? sharedFrames,
        CancellationToken ct, Action<ProbeWindowTimes>? onWindowTimed, SemaphoreSlim cpuSemaphore)
    {
        var trainResults = new ProbeResult[trainWindows.Length];

        // Bound concurrent CPU probe work with the global semaphore (--max-concurrency). The train
        // windows read from the shared in-memory buffers, so there's no per-probe ffmpeg decode to
        // serialize on — the semaphore purely caps simultaneous encode/render/PSNR CPU load.
        var trainTasks = trainWindows.Select(async (w, i) =>
        {
            await cpuSemaphore.WaitAsync(ct);
            try
            {
                trainResults[i] = await ProbeAsync(inputPath, info, fps, w.Start, w.DurationMs,
                    tileSize, hashQuantLevels, tileTolerance, colors, sharedFrames, ct, onWindowTimed);
            }
            finally
            {
                cpuSemaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(trainTasks);

        // Short-scene case: eval is the exact same window as the one and
        // only train window -- probing it a second time would just re-decode identical data for an
        // identical number. Reuse train's own result instead.
        var evalResult = trainWindows.Length == 1 && trainWindows[0] == evalWindow
            ? trainResults[0]
            : await ProbeAsync(inputPath, info, fps, evalWindow.Start, evalWindow.DurationMs,
                tileSize, hashQuantLevels, tileTolerance, colors, sharedFrames, ct, onWindowTimed);

        var train = new ProbeResult(
            trainResults.Average(r => r.Psnr),
            trainResults.Sum(r => r.AssetBytes),
            trainResults.Sum(r => r.CommandCount),
            trainResults.Sum(r => r.ElapsedMs),
            trainResults.Sum(r => r.DecodeMs),
            trainResults.Sum(r => r.RenderMs),
            trainResults.Sum(r => r.ObjectCount));
        return (train, evalResult);
    }

    private static async Task<ProbeResult> ProbeAsync(string inputPath, MediaInfo info, double fps,
        double windowStartMs, double windowDurationMs, int tileSize, int hashQuantLevels, int tileTolerance,
        int colors, IReadOnlyDictionary<(double Start, double DurationMs), List<byte[]>>? sharedFrames,
        CancellationToken ct, Action<ProbeWindowTimes>? onWindowTimed)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // inMemory: true -- a probe's PNGs exist only to learn their byte size and to let the
        // renderer read the same quantized pixels back for PSNR, never as a real deliverable (see
        // AssetStore.cs's own doc comment on the `inMemory` param for the disk-I/O cost this
        // avoids). No tempDir needed at all now -- nothing here touches the filesystem.
        var assetStore = new AssetStore("", "assets", "", pngCompressionLevel: 1, inMemory: true);
        var doc = new SbDocument();
        var mapping = new CanvasMapping(info.Width, info.Height);
        var target = new TileEncodeLoop.EmitTarget(mapping, SbLayer.Background, 0, null, doc.Add);

        // TuneAsync decoded the fixed sample windows once (see DecodeSampleWindowsAsync); this
        // probe replays those exact frames into the encode pipeline and the PSNR comparison, so
        // it never spawns ffmpeg at all. When sharedFrames is null (tune-bench --no-shared A/B
        // mode), fall back to the original per-probe decode: capture source frames via onFrame
        // while the encoder makes its own ffmpeg pass.
        var decodedFrames = sharedFrames is not null &&
                            sharedFrames.TryGetValue((windowStartMs, windowDurationMs), out var shared)
            ? shared
            : null;
        var loopOptions = new TileEncodeLoop.Options(
            inputPath, info.Width, info.Height, fps,
            TimeSpan.FromMilliseconds(windowStartMs), TimeSpan.FromMilliseconds(windowDurationMs),
            tileSize, hashQuantLevels, false, tileTolerance, 300, 0.8, false, 17_000_000, [target], colors)
            with { PreDecodedFrames = decodedFrames };

        TileEncodeLoop.EncodeStageTimes? encodeStageTimes = null;
        var decodeSw = System.Diagnostics.Stopwatch.StartNew();
        List<byte[]> sourceFrames;
        if (decodedFrames is not null)
        {
            sourceFrames = decodedFrames;
            await TileEncodeLoop.RunAsync(loopOptions, assetStore, null, ct,
                onStageTimes: times => encodeStageTimes = times);
        }
        else
        {
            sourceFrames = new List<byte[]>();
            await TileEncodeLoop.RunAsync(loopOptions, assetStore, null, ct,
                (frame, _) => sourceFrames.Add(frame.Rgb.ToArray()),
                times => encodeStageTimes = times);
        }
        var decodeMs = decodeSw.ElapsedMilliseconds;

        var reconSw = System.Diagnostics.Stopwatch.StartNew();
        var renderSw = System.Diagnostics.Stopwatch.StartNew();
        var renderer = new SoftwareStoryboardRenderer(doc, "", info.Width, info.Height, assetStore);
        var reconFrameCount = Math.Max(1, (int)Math.Ceiling(renderer.DurationMs / 1000.0 * fps));

        double psnrSum = 0;
        var compared = 0;
        var psnrSw = System.Diagnostics.Stopwatch.StartNew();
        long renderOnlyMs = 0, psnrMs = 0;

        for (; compared < sourceFrames.Count && compared < reconFrameCount; compared++)
        {
            var t = compared * 1000.0 / fps;
            renderSw.Restart();
            var canvas = renderer.RenderFrame(t);
            renderOnlyMs += renderSw.ElapsedMilliseconds;

            psnrSw.Restart();
            psnrSum += Metrics.Psnr(canvas.Rgb, sourceFrames[compared]);
            psnrMs += psnrSw.ElapsedMilliseconds;
        }

        var renderMs = reconSw.ElapsedMilliseconds;

        var psnr = compared == 0 ? 0 : psnrSum / compared;

        if (onWindowTimed is not null && encodeStageTimes is not null)
            onWindowTimed(new ProbeWindowTimes(windowStartMs, windowDurationMs, tileSize, hashQuantLevels,
                tileTolerance, colors, encodeStageTimes.FrameWaitMs, encodeStageTimes.TrackMs,
                encodeStageTimes.MergeMs, encodeStageTimes.DetectMs, encodeStageTimes.EmitMs, renderOnlyMs, psnrMs,
                sw.ElapsedMilliseconds));

        return new ProbeResult(psnr, assetStore.TotalBytes, doc.CommandCount, sw.ElapsedMilliseconds,
            decodeMs, renderMs, doc.SpriteCount + doc.AnimationCount);
    }
}

/// <summary>
///     Per-window timing breakdown collected by <c>onWindowTimed</c> during a tuning pass —
///     the benchmark command's raw material. Fields mirror <see cref="TileEncodeLoop.EncodeStageTimes" />
///     for the encode pass, then add the probe's own render/PSNR wall time and the probe total.
/// </summary>
public sealed record ProbeWindowTimes(
    double WindowStartMs,
    double WindowDurationMs,
    int TileSize,
    int HashQuantLevels,
    int TileTolerance,
    int Colors,
    long FrameWaitMs,
    long TrackMs,
    long MergeMs,
    long DetectMs,
    long EmitMs,
    long RenderMs,
    long PsnrMs,
    long ProbeMs);
