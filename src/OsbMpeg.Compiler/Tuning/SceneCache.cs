using System.Diagnostics;
using OsbMpeg.Compiler.Media;

namespace OsbMpeg.Compiler.Tuning;

public sealed record ScenePlan(double StartMs, double EndMs, TunedParameters? Tuned);

/// <summary>
///     Detects a video's scene boundaries and lazily tunes each one, in-memory only for the
///     lifetime of one VideoCompiler.CompileAsync call — no scenes.json, no cross-run persistence.
///     An earlier version of this class persisted both boundaries and tuned params to
///     {assetsRootAbs}/{VideoId}/scenes.json so a later compile (same video, same or different
///     project) could skip prepass/tuning entirely. Dropped on request: that side file, sitting
///     next to the real PNG assets a project ships, was unwanted clutter in the asset directory for
///     this tool's actual usage pattern — a compile is a one-shot run, not a long-lived pipeline
///     replaying the same footage. The real cost this used to amortize (prepass ~O(whole file
///     duration), tuning ~O(seconds per scene actually encoded) — see ParameterTuner.cs and
///     ScenePrePass.cs for what drives each) is now paid fresh every run; still bounded to only the
///     scenes actually needed within *this* run via <see cref="EnsureTunedAsync" /> (see its own doc
///     comment for why eager whole-file tuning was the real bug, not merely a missed optimization).
///     BuildAsync/EnsureTunedAsync are still worth keeping as two calls, not one: a VideoSourcePlan
///     can be referenced by several AnimationVideo entries in one .osbv document, and detection
///     (whole-file, window-independent) only needs to run once per plan per compile even though
///     tuning still varies per scene actually touched.
/// </summary>
public static class SceneCache
{
    public static Task<List<ScenePlan>> BuildAsync(string inputPath, MediaInfo info, double fps, string? hwAccel,
        Action<string>? log, CancellationToken ct) =>
        BuildCoreAsync(info.Duration.TotalMilliseconds,
            ct2 => ScenePrePass.ScanAsync(inputPath, info.Width, info.Height, fps, hwAccel, log, ct2),
            Path.GetFileName(inputPath), log, ct);

    /// <summary>
    ///     Tunes scenes[index] if it isn't already (in-memory only -- see the class doc comment for
    ///     why nothing here touches disk) and returns the (possibly newly-tuned) params. Callers
    ///     only ever touch one plan's scene list sequentially within one CompileAsync run (see
    ///     VideoCompiler.cs's own doc comment on why that's safe), so no locking here.
    /// </summary>
    public static async Task<TunedParameters> EnsureTunedAsync(string inputPath, MediaInfo info, double fps,
        List<ScenePlan> scenes, int index, string? hwAccel, Action<string>? log, CancellationToken ct)
    {
        if (scenes[index].Tuned is { } already) return already;

        var scene = scenes[index];
        var sw = Stopwatch.StartNew();
        var tuned = await ParameterTuner.TuneAsync(inputPath, info, fps, scene.StartMs, scene.EndMs,
            index == 0, hwAccel, log, ct);
        log?.Invoke($"scene [{scene.StartMs:F0},{scene.EndMs:F0}) tuned in {sw.ElapsedMilliseconds}ms");

        scenes[index] = scene with { Tuned = tuned };
        return tuned;
    }

    /// <summary>
    ///     Scene-boundary detection only, independent of decode I/O — <paramref name="scan" /> is
    ///     injected (same pattern as ParameterTuner.TuneCoreAsync's injected probe delegate) so this
    ///     is testable with a synthetic cut list, no real video decode needed.
    /// </summary>
    internal static async Task<List<ScenePlan>> BuildCoreAsync(double durationMs,
        Func<CancellationToken, Task<IReadOnlyList<double>>> scan, string label, Action<string>? log,
        CancellationToken ct)
    {
        var cuts = await scan(ct);

        var bounds = new List<double> { 0 };
        bounds.AddRange(cuts);
        bounds.Add(durationMs);

        var scenes = new List<ScenePlan>(bounds.Count - 1);
        for (var i = 0; i < bounds.Count - 1; i++)
            scenes.Add(new ScenePlan(bounds[i], bounds[i + 1], null));

        log?.Invoke($"scene scan {label}: {scenes.Count} scene(s), cuts at " +
                    string.Join(", ", cuts.Select(c => $"{c:F0}ms")));

        return scenes;
    }
}
