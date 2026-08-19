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
/// Decode is not shared across AnimationVideo objects that point at the same source (that's
/// the P6 optimization VideoSourcePlanner exists to eventually drive) — each member gets its
/// own TileEncodeLoop run. What IS shared per VideoSourcePlan is the AssetStore, so identical
/// pixel content across members of the same source still content-hash dedupes into one file,
/// same as it always would within a single video.</summary>
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

        var doc = new SbDocument();

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
                    var plan = planByMember[v];
                    var info = probeCache[Path.GetFullPath(v.FilePath)];
                    if (!assetStoreByPlan.TryGetValue(plan, out var assetStore))
                    {
                        var absoluteDir = Path.Combine(assetsRootAbs, plan.VideoId);
                        var relativeDir = $"{assetRelativeRoot}/{plan.VideoId}";
                        assetStoreByPlan[plan] = assetStore = new AssetStore(absoluteDir, relativeDir, namePrefix: "", hexNaming: true);
                    }

                    var startMs = v.VideoStartMs ?? 0;
                    var endMs = v.VideoEndMs ?? info.Duration.TotalMilliseconds;
                    var mapping = new CanvasMapping(info.Width, info.Height, v.X, v.Y);
                    var baker = v.Commands.Count > 0
                        ? new GroupTransformBaker(v.Commands, (float)v.X, (float)v.Y, plan.Key.EffectiveFps)
                        : null;
                    var loopOptions = new TileEncodeLoop.Options(
                        InputPath: v.FilePath,
                        Width: info.Width,
                        Height: info.Height,
                        Fps: plan.Key.EffectiveFps,
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
                        Layer: v.Layer,
                        StoryboardTimeOffsetMs: v.StartTimeMs,
                        Baker: baker,
                        HwAccel: hwAccel);

                    await TileEncodeLoop.RunAsync(loopOptions, doc, assetStore, mapping, onProgress: null, ct);
                    break;
            }
        }

        OsbWriter.Write(doc, osbOutputPath);
        OsbValidator.Validate(osbOutputPath, doc);

        return new VideoCompileResult(doc.SpriteCount, doc.AnimationCount, doc.CommandCount, assetStoreByPlan.Values.Sum(s => s.FileCount), plans.Count);
    }
}
