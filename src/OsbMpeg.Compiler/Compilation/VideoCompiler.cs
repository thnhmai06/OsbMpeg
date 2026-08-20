using OsbMpeg.Compiler.Encoder;
using OsbMpeg.Compiler.Media;
using OsbMpeg.Compiler.Osb;
using OsbMpeg.Compiler.Tuning;
using OsbMpeg.Parsers;
using OsbMpeg.Parsers.Ir;
using OsbMpeg.Parsers.Ir.Passes;
using OsbMpeg.Parsers.Osb;
using OsbMpeg.Parsers.Osbv;

namespace OsbMpeg.Compiler.Compilation;

public sealed record VideoCompileResult(
    int SpriteCount,
    int AnimationCount,
    int CommandCount,
    int AssetCount,
    int VideoSourceCount);

/// <summary>
///     Compiles a parsed .osbv document into a .osb + assets. Native Sprite/Animation
///     objects pass straight through to IR. Each AnimationVideo runs the same tile-grid encode as
///     the old whole-canvas CLI (TileEncodeLoop), auto-cover-placed at its declared (X,Y); if it
///     has commands, a GroupTransformBaker bakes them into every tile — see that class for what's
///     covered (Move/Scale/Rotate/Fade/Colour/Additive/flip) versus rejected (Loop).
///     Decode is shared across a VideoSourcePlan's members when they all request the identical
///     [start,end) window — the common case, since two AnimationVideo on the same file usually
///     both default to the full duration. That's the only case where sharing is provably safe
///     without touching tile-run semantics: the frame grid is identical for every member (no seek
///     phase offset to reconcile against ffmpeg's fps-resample), and one TileRunTracker/
///     AnimationDetector pair produces exactly the runs each member's own independent decode would
///     have produced, since they'd all be decoding the same frames anyway. Members with differing
///     windows fall back to today's one-decode-per-member path — reconciling a shared decodes
///     mid-stream frames against a member whose own window starts or ends partway through would
///     mean either resetting the tracker's fresh-run assumption at an arbitrary point (which a
///     truly independent decode never does) or accepting output that isn't a byte-for-byte match,
///     and no .osbv author has ever needed that case badly enough to justify it.
///     When members ARE shared, each gets its own EmitTarget (mapping/layer/offset/baker) with a
///     buffering Add sink instead of writing straight to doc — the shared decodes emission order
///     is chronological across all targets, not "all member A, then all member B" the way
///     independent per-member decode produces it, so buffering and flushing each member's objects
///     in declaration order preserves the original z-order convention (within a layer, later
///     declaration draws on top) between the shared members themselves. Known simplification: the
///     whole group lands at the position of its first member in the output layer list, so z-order
///     relative to an unrelated object interleaved between two shared members in the source .osbv
///     shifts (the unrelated object now lands after the whole group instead of between the
///     members) — harmless unless a .osbv deliberately interleaves unrelated same-layer sprites
///     between two uses of one video for a z-order effect, which nothing here does today.
/// </summary>
public static class VideoCompiler
{
    public static async Task<VideoCompileResult> CompileAsync(OsbvDocument document, string assetsRootDir,
        string osbOutputPath, string? hwAccel = null, Action<string>? log = null, CancellationToken ct = default)
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

        var animationVideos = document.Objects.OfType<OsbvAnimationVideo>().ToList();
        var plans = await VideoSourcePlanner.PlanAsync(animationVideos, Probe);
        var planByMember = plans.SelectMany(p => p.Members.Select(m => (Member: m, Plan: p)))
            .ToDictionary(t => t.Member, t => t.Plan);
        var assetStoreByPlan = new Dictionary<VideoSourcePlan, AssetStore>();
        var scenesByPlan = new Dictionary<VideoSourcePlan, List<ScenePlan>>();
        var consumedMembers = new HashSet<OsbvAnimationVideo>();

        var doc = new SbDocument();

        foreach (var obj in document.Objects)
            switch (obj)
            {
                case OsbvSprite s:
                    doc.Add(new SbSprite
                    {
                        Layer = s.Layer, Origin = s.Origin, X = (float)s.X, Y = (float)s.Y,
                        Asset = new AssetId(s.FilePath), Commands = s.Commands
                    });
                    break;

                case OsbvAnimation a:
                    doc.Add(new SbAnimation
                    {
                        Layer = a.Layer, Origin = a.Origin, X = (float)a.X, Y = (float)a.Y,
                        BasePath = new AssetId(a.FilePath), FrameCount = a.FrameCount, FrameDelayMs = a.FrameDelayMs,
                        LoopType = a.LoopType, Commands = a.Commands
                    });
                    break;

                case OsbvAnimationVideo v:
                    if (consumedMembers.Contains(v)) break; // already emitted as part of a shared-decode plan below

                    var plan = planByMember[v];
                    var info = probeCache[Path.GetFullPath(v.FilePath)];
                    var scenes = await ScenesFor(plan, info);
                    var assetStore = AssetStoreFor(plan);

                    if (plan.Members.Count > 1 && SharesWindow(plan, info))
                    {
                        await CompileSharedAsync(plan, info, doc, assetStore, scenes, hwAccel, log, ct);
                        foreach (var m in plan.Members) consumedMembers.Add(m);
                    }
                    else
                    {
                        await CompileMemberAsync(v, plan, info, doc, assetStore, scenes, hwAccel, log, ct);
                        consumedMembers.Add(v);
                    }

                    break;
            }

        MergeAdjacentCommands.Apply(doc);
        DropNoOpCommands.Apply(doc);
        LoopExtractor.Apply(doc);
        OsbWriter.Write(doc, osbOutputPath);
        OsbValidator.Validate(osbOutputPath, doc);

        return new VideoCompileResult(doc.SpriteCount, doc.AnimationCount, doc.CommandCount,
            assetStoreByPlan.Values.Sum(s => s.FileCount), plans.Count);

        async Task<MediaInfo> Probe(string path)
        {
            var normalized = Path.GetFullPath(path);
            if (!probeCache.TryGetValue(normalized, out var info))
                probeCache[normalized] = info = await MediaProbe.AnalyseAsync(normalized, ct);
            return info;
        }

        AssetStore AssetStoreFor(VideoSourcePlan plan)
        {
            if (assetStoreByPlan.TryGetValue(plan, out var existing)) return existing;
            var absoluteDir = Path.Combine(assetsRootAbs, plan.VideoId);
            var relativeDir = $"{assetRelativeRoot}/{plan.VideoId}";
            return assetStoreByPlan[plan] = new AssetStore(absoluteDir, relativeDir, "", hexNaming: true);
        }

        async Task<List<ScenePlan>> ScenesFor(VideoSourcePlan plan, MediaInfo info)
        {
            if (scenesByPlan.TryGetValue(plan, out var existing)) return existing;
            // In-memory only for this one CompileAsync call (see SceneCache.cs's own doc comment):
            // detection runs once per plan here even though multiple AnimationVideo entries can
            // reference it, and each scene's Tuned starts null, filled in lazily by
            // EnsureTunedAsync only for scenes a caller actually ends up encoding.
            var scenes = await SceneCache.BuildAsync(plan.Members[0].FilePath, info, plan.Key.EffectiveFps,
                hwAccel, log, ct);
            return scenesByPlan[plan] = scenes;
        }
    }

    private static bool SharesWindow(VideoSourcePlan plan, MediaInfo info)
    {
        var (firstStart, firstEnd) = Window(plan.Members[0], info);
        for (var i = 1; i < plan.Members.Count; i++)
        {
            var (start, end) = Window(plan.Members[i], info);
            if (!start.IsEqual(firstStart) || !end.IsEqual(firstEnd)) return false;
        }

        return true;
    }

    private static (double Start, double End) Window(OsbvAnimationVideo v, MediaInfo info)
    {
        return (v.VideoStartMs ?? 0, v.VideoEndMs ?? info.Duration.TotalMilliseconds);
    }

    private static TileEncodeLoop.Options LoopOptions(string inputPath, MediaInfo info, double fps, double startMs,
        double endMs, TunedParameters tuned, IReadOnlyList<TileEncodeLoop.EmitTarget> targets, string? hwAccel)
    {
        return new TileEncodeLoop.Options(
            inputPath,
            info.Width,
            info.Height,
            fps,
            TimeSpan.FromMilliseconds(startMs),
            TimeSpan.FromMilliseconds(endMs - startMs),
            tuned.TileSize,
            tuned.HashQuantLevels,
            false,
            tuned.TileTolerance,
            300,
            0.8,
            false,
            17_000_000,
            targets,
            tuned.Colors,
            hwAccel);
    }

    /// <summary>
    ///     One member's own [start,end) window, clipped against each scene it overlaps —
    ///     one TileEncodeLoop.RunAsync call per overlapping scene, each with that scene's own
    ///     TunedParameters (a fresh TileGrid/TileRunTracker/AnimationDetector per call — a scene
    ///     boundary is just where one windowed sub-encode ends and the next begins, the same
    ///     machinery a single non-clipped window already used). VideoFrame.Pts is always 0-based
    ///     *within its own decode's output stream* regardless of any ffmpeg seek offset (confirmed
    ///     at FrameSource.cs), so each sub-encode's own TileRun.StartMs/EndMs are local to that
    ///     sub-window and need re-anchoring to the storyboard's absolute timeline via offsetMs.
    /// </summary>
    private static async Task CompileMemberAsync(OsbvAnimationVideo v, VideoSourcePlan plan, MediaInfo info,
        SbDocument doc, AssetStore assetStore, List<ScenePlan> scenes, string? hwAccel, Action<string>? log,
        CancellationToken ct)
    {
        var (vStart, vEnd) = Window(v, info);
        var mapping = new CanvasMapping(info.Width, info.Height, v.X, v.Y);
        var baker = v.Commands.Count > 0
            ? new GroupTransformBaker(v.Commands, (float)v.X, (float)v.Y, plan.Key.EffectiveFps)
            : null;

        for (var i = 0; i < scenes.Count; i++)
        {
            var (subStart, subEnd) = Clip(vStart, vEnd, scenes[i]);
            if (subEnd <= subStart) continue;

            var tuned = await SceneCache.EnsureTunedAsync(v.FilePath, info, plan.Key.EffectiveFps, scenes, i,
                hwAccel, log, ct);

            var offsetMs = v.StartTimeMs + (subStart - vStart);
            var target = new TileEncodeLoop.EmitTarget(mapping, v.Layer, offsetMs, baker, doc.Add);
            var loopOptions = LoopOptions(v.FilePath, info, plan.Key.EffectiveFps, subStart, subEnd, tuned,
                [target], hwAccel);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await TileEncodeLoop.RunAsync(loopOptions, assetStore, null, ct);
            log?.Invoke($"encode scene [{subStart:F0},{subEnd:F0}) took {sw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>
    ///     Runs one shared decode per overlapping scene for every member of a same-window plan,
    ///     buffering each member's emitted objects (across all scenes) separately and flushing
    ///     them into doc in member (= original declaration) order once every scene has run — see
    ///     the class doc comment for why buffering is needed here and not in CompileMemberAsync.
    /// </summary>
    private static async Task CompileSharedAsync(VideoSourcePlan plan, MediaInfo info, SbDocument doc,
        AssetStore assetStore, List<ScenePlan> scenes, string? hwAccel, Action<string>? log, CancellationToken ct)
    {
        var (vStart, vEnd) = Window(plan.Members[0], info);
        var mappings = new CanvasMapping[plan.Members.Count];
        var bakers = new GroupTransformBaker?[plan.Members.Count];
        var buffers = new List<SbObject>[plan.Members.Count];

        for (var i = 0; i < plan.Members.Count; i++)
        {
            var v = plan.Members[i];
            mappings[i] = new CanvasMapping(info.Width, info.Height, v.X, v.Y);
            bakers[i] = v.Commands.Count > 0
                ? new GroupTransformBaker(v.Commands, (float)v.X, (float)v.Y, plan.Key.EffectiveFps)
                : null;
            buffers[i] = [];
        }

        for (var si = 0; si < scenes.Count; si++)
        {
            var (subStart, subEnd) = Clip(vStart, vEnd, scenes[si]);
            if (subEnd <= subStart) continue;

            var tuned = await SceneCache.EnsureTunedAsync(plan.Members[0].FilePath, info, plan.Key.EffectiveFps,
                scenes, si, hwAccel, log, ct);

            var targets = new TileEncodeLoop.EmitTarget[plan.Members.Count];
            for (var i = 0; i < plan.Members.Count; i++)
            {
                var v = plan.Members[i];
                var offsetMs = v.StartTimeMs + (subStart - vStart);
                targets[i] = new TileEncodeLoop.EmitTarget(mappings[i], v.Layer, offsetMs, bakers[i], buffers[i].Add);
            }

            var loopOptions = LoopOptions(plan.Members[0].FilePath, info, plan.Key.EffectiveFps, subStart, subEnd,
                tuned, targets, hwAccel);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await TileEncodeLoop.RunAsync(loopOptions, assetStore, null, ct);
            log?.Invoke($"encode scene [{subStart:F0},{subEnd:F0}) took {sw.ElapsedMilliseconds}ms");
        }

        foreach (var buffer in buffers)
        foreach (var o in buffer)
            doc.Add(o);
    }

    private static (double Start, double End) Clip(double vStart, double vEnd, ScenePlan scene)
    {
        return (Math.Max(vStart, scene.StartMs), Math.Min(vEnd, scene.EndMs));
    }
}