using OsbMpeg.Analysis;
using OsbMpeg.Ir;
using OsbMpeg.Media;
using OsbMpeg.Osb;
using OsbMpeg.VideoCompilation;

namespace OsbMpeg.Encoder;

/// <summary>The tile-grid conditional-replenishment loop shared by the legacy whole-canvas
/// EncodePipeline and the .osbv per-AnimationVideo compile path: decode -> track tile runs ->
/// quadtree merge -> animation-candidate detect -> emit Sprite/Animation objects into a
/// caller-supplied SbDocument. Pulled out so both callers get byte-identical encode behavior
/// for the parts they share; what varies per caller — pixel-to-storyboard mapping, target
/// layer, and a storyboard-timeline offset — is a parameter, not a fork.</summary>
public static class TileEncodeLoop
{
    public sealed record Options(
        string InputPath, int Width, int Height, double Fps, TimeSpan? Start, TimeSpan? Duration,
        int TileSize, int HashQuantLevels, bool RawSnapshot, int TileTolerance, int Gop,
        double MinAnimationUniqueness, bool NoQuadtree, long MaxAssetPixels,
        SbLayer Layer = SbLayer.Background, double StoryboardTimeOffsetMs = 0,
        GroupTransformBaker? Baker = null);

    public sealed record Result(int FrameCount, double LastFrameMs);

    public static async Task<Result> RunAsync(
        Options o, SbDocument doc, AssetStore assetStore, CanvasMapping mapping,
        Action<int, double>? onProgress, CancellationToken ct)
    {
        var grid = new TileGrid(o.Width, o.Height, o.TileSize);
        var tracker = new TileRunTracker(grid, o.HashQuantLevels, canonicalSnapshot: !o.RawSnapshot, tolerance: o.TileTolerance);
        var animationDetector = new AnimationDetector(o.Fps, maxAccumulatedFrames: o.Gop, minUniqueness: o.MinAnimationUniqueness);
        var frameOpts = new FrameSourceOptions(o.Width, o.Height, o.Fps, o.Start, o.Duration);
        var frameCount = 0;
        var lastMs = 0.0;

        await foreach (var frame in FrameSource.ReadFramesAsync(o.InputPath, frameOpts, ct))
        {
            using (frame)
            {
                lastMs = frame.Pts * 1000.0;
                frameCount++;

                var batch = tracker.Advance(frame, lastMs);
                var merged = o.NoQuadtree ? batch : QuadtreeMerger.Merge(batch, grid, o.MaxAssetPixels);
                var (sprites, animations) = animationDetector.Process(merged, o.TileSize);
                Emit(sprites, animations, doc, assetStore, mapping, o.Layer, o.StoryboardTimeOffsetMs, o.Baker);

                onProgress?.Invoke(frameCount, frame.Pts);
            }
        }

        var finalBatch = tracker.Flush(lastMs);
        var finalMerged = o.NoQuadtree ? finalBatch : QuadtreeMerger.Merge(finalBatch, grid, o.MaxAssetPixels);
        var (finalSprites, finalAnimations) = animationDetector.Process(finalMerged, o.TileSize);
        Emit(finalSprites, finalAnimations, doc, assetStore, mapping, o.Layer, o.StoryboardTimeOffsetMs, o.Baker);

        var (tailSprites, tailAnimations) = animationDetector.FlushAll();
        Emit(tailSprites, tailAnimations, doc, assetStore, mapping, o.Layer, o.StoryboardTimeOffsetMs, o.Baker);

        return new Result(frameCount, lastMs);
    }

    private static void Emit(List<TileRun> runs, List<AnimationCandidate> candidates, SbDocument doc, AssetStore assetStore, CanvasMapping mapping, SbLayer layer, double timeOffsetMs, GroupTransformBaker? baker)
    {
        foreach (var run in runs)
        {
            var asset = assetStore.GetOrAdd(run.Rgb, run.Width, run.Height, AssetConsumer.Sprite);

            if (baker is null)
            {
                var (sbX, sbY) = mapping.PixelToStoryboard(run.PixelX, run.PixelY);
                var scale = (float)mapping.StoryboardScale;
                doc.Add(new SbSprite
                {
                    Layer = layer,
                    Origin = SbOrigin.TopLeft,
                    X = (float)sbX,
                    Y = (float)sbY,
                    Asset = asset,
                    Commands = [new SbValueCommand { Kind = SbCommandKind.Scale, StartMs = run.StartMs + timeOffsetMs, EndMs = run.EndMs + timeOffsetMs, Start = scale, End = scale }],
                });
                continue;
            }

            var (centerX, centerY) = mapping.PixelToStoryboard(run.PixelX + run.Width / 2.0, run.PixelY + run.Height / 2.0);
            var (bx, by, commands) = baker.Bake(centerX, centerY, mapping.StoryboardScale, run.StartMs, run.EndMs, timeOffsetMs);
            doc.Add(new SbSprite { Layer = layer, Origin = SbOrigin.Centre, X = bx, Y = by, Asset = asset, Commands = commands });
        }

        foreach (var c in candidates)
        {
            var basePath = assetStore.WriteAnimation(c.Frames, c.Width, c.Height);

            if (baker is null)
            {
                var (sbX, sbY) = mapping.PixelToStoryboard(c.PixelX, c.PixelY);
                var scale = (float)mapping.StoryboardScale;
                doc.Add(new SbAnimation
                {
                    Layer = layer,
                    Origin = SbOrigin.TopLeft,
                    X = (float)sbX,
                    Y = (float)sbY,
                    BasePath = basePath,
                    FrameCount = c.Frames.Count,
                    FrameDelayMs = c.FrameDelayMs,
                    LoopType = SbLoopType.LoopOnce,
                    Commands = [new SbValueCommand { Kind = SbCommandKind.Scale, StartMs = c.StartMs + timeOffsetMs, EndMs = c.EndMs + timeOffsetMs, Start = scale, End = scale }],
                });
                continue;
            }

            var (centerX, centerY) = mapping.PixelToStoryboard(c.PixelX + c.Width / 2.0, c.PixelY + c.Height / 2.0);
            var (bx, by, commands) = baker.Bake(centerX, centerY, mapping.StoryboardScale, c.StartMs, c.EndMs, timeOffsetMs);
            doc.Add(new SbAnimation { Layer = layer, Origin = SbOrigin.Centre, X = bx, Y = by, BasePath = basePath, FrameCount = c.Frames.Count, FrameDelayMs = c.FrameDelayMs, LoopType = SbLoopType.LoopOnce, Commands = commands });
        }
    }
}
