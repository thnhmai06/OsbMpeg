using OsbMpeg.Compiler.Shared.Media;
using OsbMpeg.Parsers.Osbv;

namespace OsbMpeg.Compiler.Compilation;

/// <summary>
///     One shared decode job: every AnimationVideo in Members reduced to the same
///     VideoSourceKey, extracted once over their union time range.
///     No per-plan identity (a former VideoId field) is carried here anymore -- assets are stored
///     in one flat, content-addressed store shared across every plan in a compile (see AssetStore's
///     own doc comment), not partitioned per video source. A tile's hash alone decides whether it's
///     the same asset, whether it came from the same file at a different fps or from an entirely
///     different file that happened to produce byte-identical pixels.
/// </summary>
public sealed record VideoSourcePlan(
    VideoSourceKey Key,
    double SourceDurationMs,
    double UnionStartMs,
    double UnionEndMs,
    IReadOnlyList<OsbvAnimationVideo> Members);

/// <summary>
///     Groups a document's AnimationVideo objects by VideoSourceKey and computes each
///     group's union extraction window. Probing is injected as a delegate so this stays pure
///     logic, testable without ffprobe or real files — the real caller wires
///     MediaProbe.AnalyseAsync.
/// </summary>
public static class VideoSourcePlanner
{
    public static async Task<IReadOnlyList<VideoSourcePlan>> PlanAsync(
        IEnumerable<OsbvAnimationVideo> videos,
        Func<string, Task<MediaInfo>> probe)
    {
        var probeCache = new Dictionary<string, MediaInfo>(StringComparer.OrdinalIgnoreCase);
        var orderedKeys = new List<VideoSourceKey>();
        var members = new Dictionary<VideoSourceKey, List<OsbvAnimationVideo>>();
        var durations = new Dictionary<VideoSourceKey, double>();

        foreach (var v in videos)
        {
            var normalizedPath = Path.GetFullPath(v.FilePath);
            if (!probeCache.TryGetValue(normalizedPath, out var info))
                probeCache[normalizedPath] = info = await probe(normalizedPath);

            var key = VideoSourceKey.Create(normalizedPath, v.Fps, info.SourceFps);
            if (!members.TryGetValue(key, out var list))
            {
                members[key] = list = [];
                durations[key] = info.Duration.TotalMilliseconds;
                orderedKeys.Add(key);
            }

            list.Add(v);
        }

        var plans = new List<VideoSourcePlan>(orderedKeys.Count);
        foreach (var key in orderedKeys)
        {
            var group = members[key];
            var durationMs = durations[key];
            var start = group.Min(m => m.VideoStartMs ?? 0);
            var end = group.Max(m => m.VideoEndMs ?? durationMs);
            plans.Add(new VideoSourcePlan(key, durationMs, start, end, group));
        }

        return plans;
    }
}