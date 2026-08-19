using OsbMpeg.Compiler.Encoder;
using OsbMpeg.Compiler.Evaluation;
using OsbMpeg.Compiler.Media;
using OsbMpeg.Compiler.Osb;
using OsbMpeg.Compiler.Render;
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
    private const double SampleWindowMs = 1500; // real cost measured ~2x SoftwareStoryboardRenderer render time vs. encode time at 4000ms — see plan file

    private static readonly int[] TileSizeCandidates = [64, 128, 256];
    private static readonly int[] ColorsCandidates = [0, 32, 16];
    private static readonly int[] HashQuantCandidates = [32, 16, 8];
    private static readonly int[] ToleranceCandidates = [0, 4, 8, 16];

    public static async Task<TunedParameters> TuneAsync(string inputPath, MediaInfo info, double fps,
        double windowStartMs, double windowDurationMs, Action<string>? log, CancellationToken ct)
    {
        var sampleDurationMs = Math.Min(SampleWindowMs, windowDurationMs);
        var sampleStartMs = windowStartMs + Math.Max(0, (windowDurationMs - sampleDurationMs) / 2);

        return await TuneCoreAsync(
            (tileSize, hashQuantLevels, tileTolerance, colors) =>
                ProbeAsync(inputPath, info, fps, sampleStartMs, sampleDurationMs, tileSize, hashQuantLevels,
                    tileTolerance, colors, ct),
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
        var tileSize = 64;
        var hashQuantLevels = 32;
        var tileTolerance = 8;
        var colors = 0;

        var baseline = await probe(tileSize, hashQuantLevels, tileTolerance, colors);
        var floor = baseline.Psnr - TargetSlackDb;
        log?.Invoke($"tuning {label}: baseline PSNR={baseline.Psnr:F2}dB, floor={floor:F2}dB");

        (tileSize, _) = await BestAsync(TileSizeCandidates,
            v => probe(v, hashQuantLevels, tileTolerance, colors), floor, tileSize, log, nameof(tileSize));

        (colors, _) = await BestAsync(ColorsCandidates,
            v => probe(tileSize, hashQuantLevels, tileTolerance, v), floor, colors, log, nameof(colors));

        (hashQuantLevels, _) = await BestAsync(HashQuantCandidates,
            v => probe(tileSize, v, tileTolerance, colors), floor, hashQuantLevels, log, nameof(hashQuantLevels));

        // The last axis's own sweep already re-probes the fully-combined tuple for each of its
        // candidates (tileSize/colors/hashQuantLevels are all fixed by now) — so whether it found
        // anything meeting the floor at all IS the "did the whole search succeed" answer. No need
        // to re-probe the same deterministic combo again to find out.
        bool metFloor;
        (tileTolerance, metFloor) = await BestAsync(ToleranceCandidates,
            v => probe(tileSize, hashQuantLevels, v, colors), floor, tileTolerance, log, nameof(tileTolerance));

        if (!metFloor)
        {
            log?.Invoke(
                $"tuning {label}: no combo met the floor ({floor:F2}dB) — falling back to baseline defaults");
            return new TunedParameters(64, 32, 8, 0);
        }

        log?.Invoke(
            $"tuning {label}: chosen TileSize={tileSize} HashQuantLevels={hashQuantLevels} " +
            $"TileTolerance={tileTolerance} Colors={colors} (baseline PSNR={baseline.Psnr:F2}dB)");
        return new TunedParameters(tileSize, hashQuantLevels, tileTolerance, colors);
    }

    /// <summary>
    ///     Picks the candidate with the smallest combined cost among those meeting
    ///     <paramref name="floor" />; if none do, falls back to the highest-PSNR candidate (a later
    ///     axis may still recover the target) and reports <c>MetFloor: false</c> so the caller knows
    ///     this axis's own contribution never actually cleared the bar.
    /// </summary>
    private static async Task<(int Value, bool MetFloor)> BestAsync(int[] candidates,
        Func<int, Task<ProbeResult>> probe, double floor, int current, Action<string>? log, string axisName)
    {
        ProbeResult? bestPassing = null;
        var bestPassingValue = current;
        ProbeResult? bestOverall = null;
        var bestOverallValue = current;

        foreach (var candidate in candidates)
        {
            var result = await probe(candidate);
            log?.Invoke($"  {axisName}={candidate}: PSNR={result.Psnr:F2}dB cost={result.Cost:F0}");

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

        return bestPassing is not null ? (bestPassingValue, true) : (bestOverallValue, false);
    }

    internal readonly record struct ProbeResult(double Psnr, long AssetBytes, int CommandCount)
    {
        public double Cost => AssetBytes + CommandCount * BytesPerCommandEstimate;
    }

    private static async Task<ProbeResult> ProbeAsync(string inputPath, MediaInfo info, double fps,
        double windowStartMs, double windowDurationMs, int tileSize, int hashQuantLevels, int tileTolerance,
        int colors, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "osbmpeg_tune_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // AssetId is stored relative to `tempDir` (the renderer's assetRootDir below), so
            // AssetStore's own absolute write dir must be a subfolder of tempDir named the same as
            // the relativeDir string it embeds in each AssetId -- otherwise the renderer looks for
            // files one level away from where SavePng actually put them.
            var assetStore = new AssetStore(Path.Combine(tempDir, "assets"), "assets", "", colors,
                pngCompressionLevel: 1);
            var doc = new SbDocument();
            var mapping = new CanvasMapping(info.Width, info.Height);
            var target = new TileEncodeLoop.EmitTarget(mapping, SbLayer.Background, 0, null, doc.Add);
            var loopOptions = new TileEncodeLoop.Options(
                inputPath, info.Width, info.Height, fps,
                TimeSpan.FromMilliseconds(windowStartMs), TimeSpan.FromMilliseconds(windowDurationMs),
                tileSize, hashQuantLevels, false, tileTolerance, 300, 0.8, false, 17_000_000, [target]);

            // Capture each source frame's pixels as TileEncodeLoop decodes them for its own
            // purposes, instead of decoding the same short window a second time afterward just
            // for comparison — halves the ffmpeg process count per probe (this was the dominant
            // cost the first time this ran: 28 ffmpeg spawns for a 14-probe tuning pass, ~5.5
            // minutes for one 10s/720p source against a ~30-90s estimate).
            var sourceFrames = new List<byte[]>();
            await TileEncodeLoop.RunAsync(loopOptions, assetStore, null, ct,
                (frame, _) => sourceFrames.Add(frame.Rgb.ToArray()));

            var renderer = new SoftwareStoryboardRenderer(doc, tempDir, info.Width, info.Height);
            var reconFrameCount = Math.Max(1, (int)Math.Ceiling(renderer.DurationMs / 1000.0 * fps));

            double psnrSum = 0;
            var compared = 0;

            for (; compared < sourceFrames.Count && compared < reconFrameCount; compared++)
            {
                var t = compared * 1000.0 / fps;
                var canvas = renderer.RenderFrame(t);
                psnrSum += Metrics.Psnr(canvas.Rgb, sourceFrames[compared]);
            }

            var psnr = compared == 0 ? 0 : psnrSum / compared;
            return new ProbeResult(psnr, assetStore.TotalBytes, doc.CommandCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
