using OsbMpeg.Encoder;
using OsbMpeg.Ir;
using OsbMpeg.Media;
using OsbMpeg.Osb;
using OsbMpeg.Osbv;

namespace OsbMpeg.VideoCompilation;

public sealed record VideoCompileResult(int SpriteCount, int AnimationCount, int CommandCount, int AssetCount, int VideoSourceCount);

/// <summary>Compiles a parsed .osbv document into a .osb + assets. Native Sprite/Animation
/// objects pass straight through to IR. Each AnimationVideo runs the same tile-grid encode as
/// the old whole-canvas CLI (TileEncodeLoop), auto-cover-placed at its declared (X,Y); if it
/// has commands, a GroupTransformBaker bakes them into every tile — see that class for what's
/// covered (Move/Scale/Rotate/Fade/Colour/Additive/flip) versus rejected (Loop).
///
/// Decode is shared across a VideoSourcePlan's members when they all request the identical
/// [start,end) window — the common case, since two AnimationVideo on the same file usually
/// both default to the full duration. That's the only case where sharing is provably safe
/// without touching tile-run semantics: the frame grid is identical for every member (no seek
/// phase offset to reconcile against ffmpeg's fps-resample), and one TileRunTracker/
/// AnimationDetector pair produces exactly the runs each member's own independent decode would
/// have produced, since they'd all be decoding the same frames anyway. Members with differing
/// windows fall back to today's one-decode-per-member path — reconciling a shared decode's
/// mid-stream frames against a member whose own window starts or ends partway through would
/// mean either resetting the tracker's fresh-run assumption at an arbitrary point (which a
/// truly independent decode never does) or accepting output that isn't a byte-for-byte match,
/// and no .osbv author has ever needed that case badly enough to justify it.
///
/// When members ARE shared, each gets its own EmitTarget (mapping/layer/offset/baker) with a
/// buffering Add sink instead of writing straight to doc — the shared decode's emission order
/// is chronological across all targets, not "all of member A, then all of member B" the way
/// independent per-member decode produces it, so buffering and flushing each member's objects
/// in declaration order preserves the original z-order convention (within a layer, later
/// declaration draws on top) between the shared members themselves. Known simplification: the
/// whole group lands at the position of its first member in the output layer list, so z-order
/// relative to an unrelated object interleaved between two shared members in the source .osbv
/// shifts (the unrelated object now lands after the whole group instead of between the
/// members) — harmless unless a .osbv deliberately interleaves unrelated same-layer sprites
/// between two uses of one video for a z-order effect, which nothing here does today.</summary>
public static class VideoCompiler
{
    public static async Task<VideoCompileResult> CompileAsync(OsbvDocument document, string assetsRootDir, string osbOutputPath, string? hwAccel = null, CancellationToken ct = default)
    {
        var osbDir = Path.GetDirectoryName(Path.GetFullPath(osbOutputPath)) ?? ".";
        var assetsRootAbs = Path.GetFullPath(assetsRootDir);
        var assetRelativeRoot = Path.GetRelativePath(osbDir, assetsRootAbs).Replace('\\', '/');
        if (Path.IsPathRooted(assetRelativeRoot))
            throw new InvalidOperationException(
                $"Cannot express asset dir \"{assetsRootAbs}\" as a path relative to the output dir \"{osbDir}\" " +
                "(likely on different drives) — osu! needs a relative path in the .osb. Put output and assets under a common root.");
        Directory.CreateDirectory(assetsRootAbs);

        var probeCache = new Dictionary<string, MediaInfo>(StringComparer.OrdinalIgnoreCase);
        async Task<MediaInfo> Probe(string path)
        {
            var normalized = Path.GetFullPath(path);
            if (!probeCache.TryGetValue(normalized, out var info))
                probeCache[normalized] = info = await MediaProbe.AnalyseAsync(normalized, ct);
            return info;
        }

        var animationVideos = document.Objects.OfType<OsbvAnimationVideo>().ToList();
        var plans = await VideoSourcePlanner.PlanAsync(animationVideos, Probe);
        var planByMember = plans.SelectMany(p => p.Members.Select(m => (Member: m, Plan: p))).ToDictionary(t => t.Member, t => t.Plan);
        var assetStoreByPlan = new Dictionary<VideoSourcePlan, AssetStore>();
        var consumedMembers = new HashSet<OsbvAnimationVideo>();

        var doc = new SbDocument();

        AssetStore AssetStoreFor(VideoSourcePlan plan)
        {
            if (assetStoreByPlan.TryGetValue(plan, out var existing)) return existing;
            var absoluteDir = Path.Combine(assetsRootAbs, plan.VideoId);
            var relativeDir = $"{assetRelativeRoot}/{plan.VideoId}";
            return assetStoreByPlan[plan] = new AssetStore(absoluteDir, relativeDir, namePrefix: "", hexNaming: true);
        }

        foreach (var obj in document.Objects)
        {
            switch (obj)
            {
                case OsbvSprite s:
                    doc.Add(new SbSprite { Layer = s.Layer, Origin = s.Origin, X = (float)s.X, Y = (float)s.Y, Asset = new AssetId(s.FilePath), Commands = s.Commands });
                    break;

                case OsbvAnimation a:
                    doc.Add(new SbAnimation { Layer = a.Layer, Origin = a.Origin, X = (float)a.X, Y = (float)a.Y, BasePath = new AssetId(a.FilePath), FrameCount = a.FrameCount, FrameDelayMs = a.FrameDelayMs, LoopType = a.LoopType, Commands = a.Commands });
                    break;

                case OsbvAnimationVideo v:
                    if (consumedMembers.Contains(v)) break; // already emitted as part of a shared-decode plan below

                    var plan = planByMember[v];
                    var info = probeCache[Path.GetFullPath(v.FilePath)];
                    var assetStore = AssetStoreFor(plan);

                    if (plan.Members.Count > 1 && SharesWindow(plan, info))
                    {
                        await CompileSharedAsync(plan, info, doc, assetStore, hwAccel, ct);
                        foreach (var m in plan.Members) consumedMembers.Add(m);
                    }
                    else
                    {
                        await CompileMemberAsync(v, plan, info, doc, assetStore, hwAccel, ct);
                        consumedMembers.Add(v);
                    }
                    break;
            }
        }

        OsbWriter.Write(doc, osbOutputPath);
        OsbValidator.Validate(osbOutputPath, doc);

        return new VideoCompileResult(doc.SpriteCount, doc.AnimationCount, doc.CommandCount, assetStoreByPlan.Values.Sum(s => s.FileCount), plans.Count);
    }

    private static bool SharesWindow(VideoSourcePlan plan, MediaInfo info)
    {
        var (firstStart, firstEnd) = Window(plan.Members[0], info);
        for (var i = 1; i < plan.Members.Count; i++)
        {
            var (start, end) = Window(plan.Members[i], info);
            if (start != firstStart || end != firstEnd) return false;
        }
        return true;
    }

    private static (double Start, double End) Window(OsbvAnimationVideo v, MediaInfo info)
        => (v.VideoStartMs ?? 0, v.VideoEndMs ?? info.Duration.TotalMilliseconds);

    private static TileEncodeLoop.Options LoopOptions(string inputPath, MediaInfo info, double fps, double startMs, double endMs, IReadOnlyList<TileEncodeLoop.EmitTarget> targets, string? hwAccel) => new(
        InputPath: inputPath,
        Width: info.Width,
        Height: info.Height,
        Fps: fps,
        Start: TimeSpan.FromMilliseconds(startMs),
        Duration: TimeSpan.FromMilliseconds(endMs - startMs),
        TileSize: 64,
        HashQuantLevels: 32,
        RawSnapshot: false,
        TileTolerance: 8, // measured win on every fixture tested — see AssetStore's JPEG-revert note for the sibling call on asset format
        Gop: 300,
        MinAnimationUniqueness: 0.8,
        NoQuadtree: false,
        MaxAssetPixels: 17_000_000,
        Targets: targets,
        HwAccel: hwAccel);

    private static async Task CompileMemberAsync(OsbvAnimationVideo v, VideoSourcePlan plan, MediaInfo info, SbDocument doc, AssetStore assetStore, string? hwAccel, CancellationToken ct)
    {
        var (startMs, endMs) = Window(v, info);
        var mapping = new CanvasMapping(info.Width, info.Height, v.X, v.Y);
        var baker = v.Commands.Count > 0
            ? new GroupTransformBaker(v.Commands, (float)v.X, (float)v.Y, plan.Key.EffectiveFps)
            : null;
        var target = new TileEncodeLoop.EmitTarget(mapping, v.Layer, v.StartTimeMs, baker, doc.Add);
        var loopOptions = LoopOptions(v.FilePath, info, plan.Key.EffectiveFps, startMs, endMs, [target], hwAccel);

        await TileEncodeLoop.RunAsync(loopOptions, assetStore, onProgress: null, ct);
    }

    /// <summary>Runs one shared decode for every member of a same-window plan, buffering each
    /// member's emitted objects separately and flushing them into doc in member (= original
    /// declaration) order — see the class doc comment for why buffering is needed here and not
    /// in CompileMemberAsync.</summary>
    private static async Task CompileSharedAsync(VideoSourcePlan plan, MediaInfo info, SbDocument doc, AssetStore assetStore, string? hwAccel, CancellationToken ct)
    {
        var (startMs, endMs) = Window(plan.Members[0], info);
        var buffers = new List<SbObject>[plan.Members.Count];
        var targets = new TileEncodeLoop.EmitTarget[plan.Members.Count];

        for (var i = 0; i < plan.Members.Count; i++)
        {
            var v = plan.Members[i];
            var mapping = new CanvasMapping(info.Width, info.Height, v.X, v.Y);
            var baker = v.Commands.Count > 0
                ? new GroupTransformBaker(v.Commands, (float)v.X, (float)v.Y, plan.Key.EffectiveFps)
                : null;
            var buffer = buffers[i] = [];
            targets[i] = new TileEncodeLoop.EmitTarget(mapping, v.Layer, v.StartTimeMs, baker, buffer.Add);
        }

        var loopOptions = LoopOptions(plan.Members[0].FilePath, info, plan.Key.EffectiveFps, startMs, endMs, targets, hwAccel);
        await TileEncodeLoop.RunAsync(loopOptions, assetStore, onProgress: null, ct);

        foreach (var buffer in buffers)
            foreach (var o in buffer)
                doc.Add(o);
    }
}
