using OsbMpeg.Compiler.Compilation;
using OsbMpeg.Compiler.Shared.Analysis;
using OsbMpeg.Compiler.Shared.Media;
using OsbMpeg.Parsers.Ir;

namespace OsbMpeg.Compiler.Encode;

/// <summary>
///     The tile-grid conditional-replenishment loop shared by the legacy whole-canvas
///     EncodePipeline and the .osbv per-AnimationVideo compile path: decode -> track tile runs ->
///     quadtree merge -> animation-candidate detect -> emit Sprite/Animation objects into one or
///     more caller-supplied sinks. Pulled out so callers get byte-identical encode behavior for
///     the parts they share; what varies per caller — pixel-to-storyboard mapping, target layer, a
///     storyboard-timeline offset, and where the resulting objects land — is a list of targets, not
///     a fork. A single-element target list (every caller except VideoCompiler's shared-decode
///     path) reproduces the old single-mapping behavior exactly.
/// </summary>
public static class TileEncodeLoop
{
    public static async Task<Result> RunAsync(
        Options o, AssetStore assetStore,
        Action<int, double>? onProgress, CancellationToken ct, Action<VideoFrame, double>? onFrame = null,
        Action<EncodeStageTimes>? onStageTimes = null)
    {
        var grid = new TileGrid(o.Width, o.Height, o.TileSize);
        var tracker = new TileRunTracker(grid, o.HashQuantLevels, !o.RawSnapshot, o.TileTolerance);
        var animationDetector =
            new AnimationDetector(o.Fps, maxAccumulatedFrames: o.Gop, minUniqueness: o.MinAnimationUniqueness);
        var frameOpts = new FrameSourceOptions(o.Width, o.Height, o.Fps, o.Start, o.Duration,
            o.HwAccel is { } hw ? $"-hwaccel {hw}" : null);
        var frameCount = 0;
        var lastMs = 0.0;

        // Opt-in stage instrumentation (onStageTimes == null => zero overhead, no Stopwatch kept).
        // FrameWaitMs is wall time spent *waiting* for the frame source to deliver the next frame
        // (ffmpeg spawn/seek/decode/pipe for the first, stream teardown for the last; ~zero when
        // frames come from PreDecodedFrames) -- everything in this pass that is not one of the CPU
        // stages below. Track/Merge/Detect/Emit cover the per-frame + final-flush encode work.
        var stageSw = onStageTimes is null ? null : System.Diagnostics.Stopwatch.StartNew();
        var stageTimes = onStageTimes is null ? null : new EncodeStageTimes();
        long frameReadyMs = 0;
        long afterEmitMs = 0;

        // PreDecodedFrames (ParameterTuner's shared sample windows, see TuneAsync): the probe
        // already captured these exact frames once, so replay them from memory instead of
        // spawning ffmpeg again -- identical bytes, zero re-decode.
        var frames = o.PreDecodedFrames is not null
            ? FrameSource.ReadBuffersAsync(o.PreDecodedFrames, frameOpts, ct)
            : FrameSource.ReadFramesAsync(o.InputPath, frameOpts, ct);

        await foreach (var frame in frames.WithCancellation(ct))
            using (frame)
            {
                if (stageTimes is not null)
                {
                    frameReadyMs = stageSw!.ElapsedMilliseconds;
                    stageTimes.FrameWaitMs += frameReadyMs - afterEmitMs; // first frame includes ffmpeg startup
                }

                lastMs = frame.Pts * 1000.0;
                frameCount++;
                onFrame?.Invoke(frame, lastMs);

                var batch = tracker.Advance(frame, lastMs);
                if (stageTimes is not null)
                {
                    var t1 = stageSw!.ElapsedMilliseconds;
                    stageTimes.TrackMs += t1 - frameReadyMs;
                    frameReadyMs = t1;
                }

                var merged = o.NoQuadtree
                    ? batch
                    : QuadtreeMerger.Merge(batch, grid, o.MaxAssetPixels, 1000.0 / o.Fps);
                if (stageTimes is not null)
                {
                    var t2 = stageSw!.ElapsedMilliseconds;
                    stageTimes.MergeMs += t2 - frameReadyMs;
                    frameReadyMs = t2;
                }

                var (sprites, animations) = animationDetector.Process(merged, o.TileSize);
                if (stageTimes is not null)
                {
                    var t3 = stageSw!.ElapsedMilliseconds;
                    stageTimes.DetectMs += t3 - frameReadyMs;
                    frameReadyMs = t3;
                }

                Emit(sprites, animations, assetStore, o.Targets, o.Colors);
                if (stageTimes is not null)
                {
                    afterEmitMs = stageSw!.ElapsedMilliseconds;
                    stageTimes.EmitMs += afterEmitMs - frameReadyMs;
                    stageTimes.Frames = frameCount;
                }

                onProgress?.Invoke(frameCount, frame.Pts);
            }

        if (stageTimes is not null)
        {
            // Stream teardown after the last frame -- part of the decode-side cost, not our CPU.
            stageTimes.FrameWaitMs += stageSw!.ElapsedMilliseconds - afterEmitMs;
            frameReadyMs = stageSw.ElapsedMilliseconds;
        }

        var finalBatch = tracker.Flush(lastMs);
        if (stageTimes is not null)
        {
            var f1 = stageSw!.ElapsedMilliseconds;
            stageTimes.TrackMs += f1 - frameReadyMs;
            frameReadyMs = f1;
        }

        var finalMerged = o.NoQuadtree
            ? finalBatch
            : QuadtreeMerger.Merge(finalBatch, grid, o.MaxAssetPixels, 1000.0 / o.Fps);
        if (stageTimes is not null)
        {
            var f2 = stageSw!.ElapsedMilliseconds;
            stageTimes.MergeMs += f2 - frameReadyMs;
            frameReadyMs = f2;
        }

        var (finalSprites, finalAnimations) = animationDetector.Process(finalMerged, o.TileSize);
        if (stageTimes is not null)
        {
            var f3 = stageSw!.ElapsedMilliseconds;
            stageTimes.DetectMs += f3 - frameReadyMs;
            frameReadyMs = f3;
        }

        Emit(finalSprites, finalAnimations, assetStore, o.Targets, o.Colors);
        if (stageTimes is not null)
        {
            var f4 = stageSw!.ElapsedMilliseconds;
            stageTimes.EmitMs += f4 - frameReadyMs;
            frameReadyMs = f4;
        }

        var (tailSprites, tailAnimations) = animationDetector.FlushAll();
        if (stageTimes is not null)
        {
            var f5 = stageSw!.ElapsedMilliseconds;
            stageTimes.DetectMs += f5 - frameReadyMs;
            frameReadyMs = f5;
        }

        Emit(tailSprites, tailAnimations, assetStore, o.Targets, o.Colors);
        if (stageTimes is not null)
        {
            stageTimes.EmitMs += stageSw!.ElapsedMilliseconds - frameReadyMs;
            onStageTimes?.Invoke(stageTimes);
        }

        return new Result(frameCount, lastMs);
    }

    private static void Emit(List<TileRun> runs, List<AnimationCandidate> candidates, AssetStore assetStore,
        IReadOnlyList<EmitTarget> targets, int colors)
    {
        foreach (var run in runs)
        {
            // Content-hash dedupe is shared across targets on purpose — identical pixels stay
            // one file regardless of how many targets place a sprite over them.
            var asset = assetStore.GetOrAdd(run.Rgb, run.Width, run.Height, AssetConsumer.Sprite, colors);

            foreach (var target in targets)
            {
                if (target.Baker is null)
                {
                    var (sbX, sbY) = target.Mapping.PixelToStoryboard(run.PixelX, run.PixelY);
                    var scale = (float)target.Mapping.StoryboardScale;
                    target.Add(new SbSprite
                    {
                        Layer = target.Layer,
                        Origin = SbOrigin.TopLeft,
                        X = (float)sbX,
                        Y = (float)sbY,
                        Asset = asset,
                        Commands =
                        [
                            new SbValueCommand
                            {
                                Kind = SbCommandKind.Scale, StartMs = run.StartMs + target.StoryboardTimeOffsetMs,
                                EndMs = run.EndMs + target.StoryboardTimeOffsetMs, Start = scale, End = scale
                            }
                        ]
                    });
                    continue;
                }

                var (centerX, centerY) =
                    target.Mapping.PixelToStoryboard(run.PixelX + run.Width / 2.0, run.PixelY + run.Height / 2.0);
                var (bx, by, commands) = target.Baker.Bake(centerX, centerY, target.Mapping.StoryboardScale,
                    run.StartMs, run.EndMs, target.StoryboardTimeOffsetMs);
                target.Add(new SbSprite
                {
                    Layer = target.Layer, Origin = SbOrigin.Centre, X = bx, Y = by, Asset = asset, Commands = commands
                });
            }
        }

        foreach (var c in candidates)
            // Unlike sprite assets, Animation frame files aren't content-hash deduped (the
            // format needs a numbered file series per Animation object — see AssetStore's own
            // notes on why Sprite and Animation can't share a file) — each target still needs
            // its own WriteAnimation call, same as independent per-target decode would produce.
        foreach (var target in targets)
        {
            var basePath = assetStore.WriteAnimation(c.Frames, c.Width, c.Height, colors);

            if (target.Baker is null)
            {
                var (sbX, sbY) = target.Mapping.PixelToStoryboard(c.PixelX, c.PixelY);
                var scale = (float)target.Mapping.StoryboardScale;
                target.Add(new SbAnimation
                {
                    Layer = target.Layer,
                    Origin = SbOrigin.TopLeft,
                    X = (float)sbX,
                    Y = (float)sbY,
                    BasePath = basePath,
                    FrameCount = c.Frames.Count,
                    FrameDelayMs = c.FrameDelayMs,
                    LoopType = SbLoopType.LoopOnce,
                    Commands =
                    [
                        new SbValueCommand
                        {
                            Kind = SbCommandKind.Scale, StartMs = c.StartMs + target.StoryboardTimeOffsetMs,
                            EndMs = c.EndMs + target.StoryboardTimeOffsetMs, Start = scale, End = scale
                        }
                    ]
                });
                continue;
            }

            var (centerX, centerY) =
                target.Mapping.PixelToStoryboard(c.PixelX + c.Width / 2.0, c.PixelY + c.Height / 2.0);
            var (bx, by, commands) = target.Baker.Bake(centerX, centerY, target.Mapping.StoryboardScale, c.StartMs,
                c.EndMs, target.StoryboardTimeOffsetMs);
            target.Add(new SbAnimation
            {
                Layer = target.Layer, Origin = SbOrigin.Centre, X = bx, Y = by, BasePath = basePath,
                FrameCount = c.Frames.Count, FrameDelayMs = c.FrameDelayMs, LoopType = SbLoopType.LoopOnce,
                Commands = commands
            });
        }
    }

    /// <summary>
    ///     One consumer of this decode's tile runs. Multiple targets let one decode pass
    ///     serve several AnimationVideo objects that share a source (same file + fps, identical
    ///     [start,end) window) — each gets its own placement/layer/offset/baker, and its own Add
    ///     sink so the caller can buffer per-target and control final document order instead of
    ///     letting emission order be whatever the shared decode's chronological interleaving is.
    /// </summary>
    public sealed record EmitTarget(
        CanvasMapping Mapping,
        SbLayer Layer,
        double StoryboardTimeOffsetMs,
        GroupTransformBaker? Baker,
        Action<SbObject> Add);

    public sealed record Options(
        string InputPath,
        int Width,
        int Height,
        double Fps,
        TimeSpan? Start,
        TimeSpan? Duration,
        int TileSize,
        int HashQuantLevels,
        bool RawSnapshot,
        int TileTolerance,
        int Gop,
        double MinAnimationUniqueness,
        bool NoQuadtree,
        long MaxAssetPixels,
        IReadOnlyList<EmitTarget> Targets,
        int Colors = 0,
        string? HwAccel = null,
        IReadOnlyList<byte[]>? PreDecodedFrames = null);

    /// <summary>
    ///     Opt-in per-stage timing breakdown of a single <see cref="RunAsync" /> pass (only
    ///     populated/fired when the run was given an <c>onStageTimes</c> callback, so the normal
    ///     encode path pays zero for it). Stage boundaries are measured in the order the work
    ///     happens per frame — FrameWait (waiting on the frame source), Track, Merge, Detect,
    ///     Emit — and the final flush + residue passes are folded into the same stages. Sums to
    ///     the pass's wall time (minus the caller's own onFrame/onProgress overhead).
    /// </summary>
    public sealed class EncodeStageTimes
    {
        public long FrameWaitMs { get; set; }
        public long TrackMs { get; set; }
        public long MergeMs { get; set; }
        public long DetectMs { get; set; }
        public long EmitMs { get; set; }
        public int Frames { get; set; }
    }

    public sealed record Result(int FrameCount, double LastFrameMs);
}