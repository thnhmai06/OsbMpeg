using System.Text.Json;
using OsbMpeg.Compiler.Media;

namespace OsbMpeg.Compiler.Tuning;

public sealed record ScenePlan(double StartMs, double EndMs, TunedParameters Tuned);

/// <summary>
///     Persists a video's detected scene boundaries + each scene's own tuned params, keyed by
///     video file (the same {assetsRootAbs}/{VideoId} directory AssetStore already writes its
///     asset cache into — a natural fit now that VideoId is a stable hash of the video source,
///     not a per-document counter). First compile of a video pays ScenePrePass's decode-only scan
///     plus one ParameterTuner.TuneAsync call per detected scene; every later compile of that video
///     — the same project re-running, or a *different* .osbv project referencing the same file —
///     reads the cache file and pays nothing further. Same amortization argument that already
///     justifies the persistent, content-addressed AssetStore.
///     DetectorVersion guards against silently reusing a stale cache after ScenePrePass's own
///     constants (CutThreshold, MinSceneMs, the baseline reference combo) change — same
///     invalidate-the-whole-thing-on-constant-change pattern AssetStore already relies on for
///     --tile-size.
/// </summary>
public static class SceneCache
{
    private const int DetectorVersion = 3;

    private sealed record CacheFile(int DetectorVersion, List<CachedScene> Scenes);

    private sealed record CachedScene(double StartMs, double EndMs, int TileSize, int HashQuantLevels,
        int TileTolerance, int Colors);

    public static async Task<IReadOnlyList<ScenePlan>> LoadOrBuildAsync(string videoCacheDir, string inputPath,
        MediaInfo info, double fps, Action<string>? log, CancellationToken ct)
    {
        var cachePath = Path.Combine(videoCacheDir, "scenes.json");

        if (File.Exists(cachePath))
        {
            var cached = JsonSerializer.Deserialize<CacheFile>(await File.ReadAllTextAsync(cachePath, ct));
            if (cached is { DetectorVersion: DetectorVersion })
                return cached.Scenes
                    .Select(s => new ScenePlan(s.StartMs, s.EndMs,
                        new TunedParameters(s.TileSize, s.HashQuantLevels, s.TileTolerance, s.Colors)))
                    .ToList();
        }

        var built = await BuildCoreAsync(info.Duration.TotalMilliseconds,
            ct2 => ScenePrePass.ScanAsync(inputPath, info.Width, info.Height, fps, ct2),
            (start, end, isFirst, ct2) => ParameterTuner.TuneAsync(inputPath, info, fps, start, end, isFirst, log, ct2),
            Path.GetFileName(inputPath), log, ct);

        Directory.CreateDirectory(videoCacheDir);
        var toWrite = new CacheFile(DetectorVersion,
            built.Select(s => new CachedScene(s.StartMs, s.EndMs, s.Tuned.TileSize, s.Tuned.HashQuantLevels,
                s.Tuned.TileTolerance, s.Tuned.Colors)).ToList());
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(toWrite), ct);

        return built;
    }

    /// <summary>
    ///     The actual scene-bounds + per-scene-tuning decision logic, independent of decode/probe
    ///     I/O — <paramref name="scan" />/<paramref name="tune" /> are injected (same pattern as
    ///     ParameterTuner.TuneCoreAsync's injected probe delegate) so this is testable with
    ///     synthetic cut lists and tuned combos, no real video decode needed.
    /// </summary>
    internal static async Task<IReadOnlyList<ScenePlan>> BuildCoreAsync(double durationMs,
        Func<CancellationToken, Task<IReadOnlyList<double>>> scan,
        Func<double, double, bool, CancellationToken, Task<TunedParameters>> tune,
        string label, Action<string>? log, CancellationToken ct)
    {
        var cuts = await scan(ct);

        var bounds = new List<double> { 0 };
        bounds.AddRange(cuts);
        bounds.Add(durationMs);

        var scenes = new List<ScenePlan>(bounds.Count - 1);
        for (var i = 0; i < bounds.Count - 1; i++)
        {
            var start = bounds[i];
            var end = bounds[i + 1];
            var tuned = await tune(start, end, i == 0, ct);
            scenes.Add(new ScenePlan(start, end, tuned));
        }

        log?.Invoke($"scene scan {label}: {scenes.Count} scene(s), cuts at " +
                    string.Join(", ", cuts.Select(c => $"{c:F0}ms")));

        return scenes;
    }
}
