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
///     defaults (TileSize=64, HashQuantLevels=32, TileTolerance=8, Colors=0) on a short sample
///     window, use that PSNR (minus <see cref="TargetSlackDb" />) as the floor every candidate must
///     clear. A hard zero-slack floor would make every lossy-but-cheaper candidate (any Colors
///     quantization, any TileTolerance above the baseline's own 8) score below the baseline by
///     construction and never win — the slack is what lets the search actually find something
///     cheaper instead of always falling back to the baseline it started from.
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
///     initially missed: the first end-to-end run (before this) spent ~5.5 minutes tuning one
///     10s/720p source (28 ffmpeg spawns for 14 probes) against a ~30-90s estimate; capturing
///     frames during the encode's own decode instead of a separate pass halves that. Each
///     candidate writes its throwaway PNGs to a fresh temp dir, deleted immediately after — never
///     touches the real, persistent, content-addressed AssetStore (see AssetStore.cs's own doc
///     comment: a different TileSize hashes completely differently anyway, so there's no reuse to
///     be had during search, only pollution to avoid).
/// </summary>
public static class ParameterTuner
{
    private const double TargetSlackDb = 1.0;
    private const double BytesPerCommandEstimate = 100; // fish_spin_test: 44.63KB / 445 commands ≈ 100.3, this session
    // Also used by Detection (ScenePrePass's margin-fetch, via VideoCompiler) as the "how much
    // material does one scene's tuning sample need" placeholder -- real cost measured ~2x
    // SoftwareStoryboardRenderer render time vs. encode time at 4000ms — see docs/research.md.
    // Not re-benchmarked since; revisit once a real train/val/test sampling design exists.
    internal const double SampleWindowMs = 1500;

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
    ///     was, not a parallel path (this API used to be file-duration-centered with no segment
    ///     concept; that behavior is reproduced exactly by that one-segment case).
    ///     The first segment samples a window centered within itself, same as the old whole-file
    ///     design, to avoid an atypical intro/title-card. Every later segment (which always starts
    ///     right after a real cut) samples starting at its own beginning instead —
    ///     anchoring there folds that segment's own one-time full-canvas re-emit cost into its own
    ///     probe; a mid-segment sample would never see it, silently under-costing a big TileSize
    ///     candidate that looks cheap in isolation but is expensive specifically at the cut it owns.
    /// </summary>
    public static async Task<TunedParameters> TuneAsync(string inputPath, MediaInfo info, double fps,
        double segmentStartMs, double segmentEndMs, bool isFirstSegment, string? hwAccel, Action<string>? log,
        CancellationToken ct)
    {
        var segmentDurationMs = segmentEndMs - segmentStartMs;
        var sampleDurationMs = Math.Min(SampleWindowMs, segmentDurationMs);
        var sampleStartMs = isFirstSegment
            ? segmentStartMs + Math.Max(0, (segmentDurationMs - sampleDurationMs) / 2)
            : segmentStartMs;

        return await TuneCoreAsync(
            (tileSize, hashQuantLevels, tileTolerance, colors) =>
                ProbeAsync(inputPath, info, fps, sampleStartMs, sampleDurationMs, tileSize, hashQuantLevels,
                    tileTolerance, colors, hwAccel, ct),
            Path.GetFileName(inputPath), log);
    }

    /// <summary>
    ///     The search itself, independent of ffmpeg/rendering — <paramref name="probe" /> is
    ///     injected (same pattern as <c>VideoSourcePlanner.PlanAsync</c>'s injected probe delegate)
    ///     so the coordinate-descent decision logic (slack floor, cost comparison, fallback-to-best,
    ///     probe budget) is testable with synthetic <see cref="ProbeResult" />s, no real video decode
    ///     needed.
    /// </summary>
    internal static async Task<TunedParameters> TuneCoreAsync(
        Func<int, int, int, int, Task<ProbeResult>> probe, string label, Action<string>? log)
    {
        var hashQuantLevels = 32;
        var tileTolerance = 8;
        var colors = 0;

        // TileSize=64 with the other 3 params at their defaults IS today's baseline combo, and
        // it's already one of TileSizeCandidates — probe the whole axis in one concurrent batch
        // and pull the baseline out of whichever result comes back for value 64, instead of a
        // separate blocking probe that used to run in front of this axis (re-probing that exact
        // same (64,32,8,0) tuple a second time before the axis's own sweep even started).
        var tileSizeResults = await Task.WhenAll(
            TileSizeCandidates.Select(v => probe(v, hashQuantLevels, tileTolerance, colors)));
        var baseline = tileSizeResults[Array.IndexOf(TileSizeCandidates, 64)];
        var floor = baseline.Psnr - TargetSlackDb;
        log?.Invoke($"tuning {label}: baseline PSNR={baseline.Psnr:F2}dB, floor={floor:F2}dB");

        var (tileSize, tileSizeWin, _) =
            Select(TileSizeCandidates, tileSizeResults, floor, 64, log, "tileSize");

        // Every later axis's "unchanged" candidate — the value already fixed on entry — probes
        // the exact same tuple the previous axis's own winning candidate already resolved (same
        // params, nothing new varies). Carry that ProbeResult forward as a seed so BestAsync
        // skips re-probing it instead of paying for the same deterministic combo twice.
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
                $"tuning {label}: no combo met the floor ({floor:F2}dB) — falling back to baseline defaults");
            return new TunedParameters(64, 32, 8, 0);
        }

        log?.Invoke(
            $"tuning {label}: chosen TileSize={tileSize} HashQuantLevels={hashQuantChosen} " +
            $"TileTolerance={toleranceChosen} Colors={colorsChosen} (baseline PSNR={baseline.Psnr:F2}dB)");
        return new TunedParameters(tileSize, hashQuantChosen, toleranceChosen, colorsChosen);
    }

    /// <summary>
    ///     Probes every candidate except <paramref name="current" /> — that one's result is
    ///     already known (<paramref name="seed" />, the previous axis's own winning probe, which
    ///     necessarily used the exact same tuple this axis's unchanged candidate would probe
    ///     again) — then hands the assembled results to <see cref="Select" />.
    ///     Candidates within one axis are independent (each writes to its own throwaway temp dir,
    ///     no shared mutable state), so they run concurrently via Task.WhenAll rather than one at a
    ///     time — axes still run sequentially against each other (each one's candidates need the
    ///     previous axis's chosen value), but this is the one place probes can overlap without
    ///     changing what the search decides.
    /// </summary>
    private static async Task<(int Value, ProbeResult Result, bool MetFloor)> BestAsync(int[] candidates,
        Func<int, Task<ProbeResult>> probe, double floor, int current, ProbeResult seed, Action<string>? log,
        string axisName)
    {
        var seedIndex = Array.IndexOf(candidates, current);
        var tasks = new Task<ProbeResult>?[candidates.Length];
        for (var i = 0; i < candidates.Length; i++)
            if (i != seedIndex)
                tasks[i] = probe(candidates[i]);

        await Task.WhenAll(tasks.Where(t => t is not null)!);

        var results = new ProbeResult[candidates.Length];
        for (var i = 0; i < candidates.Length; i++)
            results[i] = i == seedIndex ? seed : tasks[i]!.Result;

        return Select(candidates, results, floor, current, log, axisName);
    }

    /// <summary>
    ///     Picks the candidate with the smallest combined cost among those meeting
    ///     <paramref name="floor" />; if none do, falls back to the highest-PSNR candidate (a later
    ///     axis may still recover the target) and reports <c>MetFloor: false</c> so the caller knows
    ///     this axis's own contribution never actually cleared the bar. Task.WhenAll (in
    ///     <see cref="BestAsync" />) preserves input order in its result array regardless of
    ///     completion order, so tie-breaking (first-seen-wins on equal cost) is unaffected by
    ///     probing concurrently.
    /// </summary>
    private static (int Value, ProbeResult Result, bool MetFloor) Select(int[] candidates, ProbeResult[] results,
        double floor, int current, Action<string>? log, string axisName)
    {
        ProbeResult? bestPassing = null;
        var bestPassingValue = current;
        ProbeResult? bestOverall = null;
        var bestOverallValue = current;

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            var result = results[i];
            log?.Invoke($"  {axisName}={candidate}: PSNR={result.Psnr:F2}dB cost={result.Cost:F0} " +
                        $"took={result.ElapsedMs}ms (decode={result.DecodeMs}ms render+psnr={result.RenderMs}ms objects={result.ObjectCount})");

            if (bestOverall is null || result.Psnr > bestOverall.Value.Psnr)
            {
                bestOverall = result;
                bestOverallValue = candidate;
            }

            if (result.Psnr >= floor && (bestPassing is null || result.Cost < bestPassing.Value.Cost))
            {
                bestPassing = result;
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

    private static async Task<ProbeResult> ProbeAsync(string inputPath, MediaInfo info, double fps,
        double windowStartMs, double windowDurationMs, int tileSize, int hashQuantLevels, int tileTolerance,
        int colors, string? hwAccel, CancellationToken ct)
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
        var loopOptions = new TileEncodeLoop.Options(
            inputPath, info.Width, info.Height, fps,
            TimeSpan.FromMilliseconds(windowStartMs), TimeSpan.FromMilliseconds(windowDurationMs),
            tileSize, hashQuantLevels, false, tileTolerance, 300, 0.8, false, 17_000_000, [target], colors,
            hwAccel);

        // Capture each source frame's pixels as TileEncodeLoop decodes them for its own
        // purposes, instead of decoding the same short window a second time afterward just
        // for comparison — halves the ffmpeg process count per probe (this was the dominant
        // cost the first time this ran: 28 ffmpeg spawns for a 14-probe tuning pass, ~5.5
        // minutes for one 10s/720p source against a ~30-90s estimate).
        var decodeSw = System.Diagnostics.Stopwatch.StartNew();
        var sourceFrames = new List<byte[]>();
        await TileEncodeLoop.RunAsync(loopOptions, assetStore, null, ct,
            (frame, _) => sourceFrames.Add(frame.Rgb.ToArray()));
        var decodeMs = decodeSw.ElapsedMilliseconds;

        var renderSw = System.Diagnostics.Stopwatch.StartNew();
        var renderer = new SoftwareStoryboardRenderer(doc, "", info.Width, info.Height, assetStore);
        var reconFrameCount = Math.Max(1, (int)Math.Ceiling(renderer.DurationMs / 1000.0 * fps));

        double psnrSum = 0;
        var compared = 0;

        for (; compared < sourceFrames.Count && compared < reconFrameCount; compared++)
        {
            var t = compared * 1000.0 / fps;
            var canvas = renderer.RenderFrame(t);
            psnrSum += Metrics.Psnr(canvas.Rgb, sourceFrames[compared]);
        }

        var renderMs = renderSw.ElapsedMilliseconds;

        var psnr = compared == 0 ? 0 : psnrSum / compared;
        return new ProbeResult(psnr, assetStore.TotalBytes, doc.CommandCount, sw.ElapsedMilliseconds,
            decodeMs, renderMs, doc.SpriteCount + doc.AnimationCount);
    }
}
