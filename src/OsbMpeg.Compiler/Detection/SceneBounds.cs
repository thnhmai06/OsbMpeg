using OsbMpeg.Compiler.Tuning;

namespace OsbMpeg.Compiler.Detection;

public sealed record ScenePlan(double StartMs, double EndMs, TunedParameters? Tuned);

/// <summary>
///     Turns a ScenePrePassResult (effective [StartMs,EndMs) range + internal cut timestamps) into
///     the list of ScenePlans VideoCompiler actually iterates — one per sub-range between
///     consecutive cuts, each starting untuned (filled in lazily, see VideoCompiler's own
///     TunedFor).
/// </summary>
public static class SceneBounds
{
    /// <summary>
    ///     <paramref name="scan" /> is injected (same pattern as ParameterTuner.TuneCoreAsync's
    ///     injected probe delegate) so this is testable with a synthetic scan result, no real video
    ///     decode needed.
    /// </summary>
    internal static async Task<List<ScenePlan>> BuildCoreAsync(
        Func<CancellationToken, Task<ScenePrePassResult>> scan, string label, Action<string>? log,
        CancellationToken ct)
    {
        var result = await scan(ct);

        var bounds = new List<double> { result.StartMs };
        bounds.AddRange(result.Cuts);
        bounds.Add(result.EndMs);

        var scenes = new List<ScenePlan>(bounds.Count - 1);
        for (var i = 0; i < bounds.Count - 1; i++)
            scenes.Add(new ScenePlan(bounds[i], bounds[i + 1], null));

        log?.Invoke($"scene scan {label}: {scenes.Count} scene(s), cuts at " +
                    string.Join(", ", result.Cuts.Select(c => $"{c:F0}ms")));

        return scenes;
    }
}
