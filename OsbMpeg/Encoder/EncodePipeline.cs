using System.Diagnostics;
using OsbMpeg.Analysis;
using OsbMpeg.Ir;
using OsbMpeg.Media;
using OsbMpeg.Osb;

namespace OsbMpeg.Encoder;

/// <summary>Orchestrates the MVP encode path: stream decoded frames, run the tile-grid
/// conditional-replenishment backbone (Sprite-per-run + content-hash dedupe — see the design
/// notes for why this beats Animation-first as the default), write IR straight to .osb, and
/// round-trip-validate the result. No motion/region/RDO layer yet — those are optimizer-phase
/// candidates that must win on cost, not baseline dependencies.</summary>
public sealed class EncodePipeline(EncodeOptions options)
{
    public async Task<EncodeStatistics> RunAsync(Action<EncodeProgress>? onProgress, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var probe = await MediaProbe.AnalyseAsync(options.InputPath, ct);
        var width = options.KeepSource ? probe.Width : options.CanvasWidth;
        var height = options.KeepSource ? probe.Height : options.CanvasHeight;
        var fps = options.KeepSource ? probe.SourceFps : options.Fps;
        var effectiveDuration = options.Duration ?? probe.Duration - (options.Start ?? TimeSpan.Zero);
        var estimatedTotalFrames = Math.Max(1, (int)(effectiveDuration.TotalSeconds * fps));

        var outputDir = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(options.AssetDir);

        var assetStore = new AssetStore(options.AssetDir, options.AssetRelativeDir, options.AssetNamePrefix, options.Colors, options.PngCompressionLevel);
        var doc = new SbDocument { Assets = assetStore };
        var mapping = new CanvasMapping(width, height);
        var grid = new TileGrid(width, height, options.TileSize);
        var tracker = new TileRunTracker(grid, options.HashQuantLevels);
        var animationDetector = new AnimationDetector(fps, maxAccumulatedFrames: options.Gop);

        var frameOpts = new FrameSourceOptions(width, height, fps, options.Start, options.Duration);
        var frameCount = 0;
        var lastMs = 0.0;

        await foreach (var frame in FrameSource.ReadFramesAsync(options.InputPath, frameOpts, ct))
        {
            using (frame)
            {
                lastMs = frame.Pts * 1000.0;
                frameCount++;

                var merged = QuadtreeMerger.Merge(tracker.Advance(frame, lastMs), grid, options.MaxAssetPixels);
                var (sprites, animations) = animationDetector.Process(merged, options.TileSize);
                EmitRuns(sprites, doc, assetStore, mapping);
                EmitAnimations(animations, doc, assetStore, mapping);

                onProgress?.Invoke(new EncodeProgress(frameCount, estimatedTotalFrames, frame.Pts, doc.SpriteCount, doc.CommandCount, assetStore.FileCount, assetStore.TotalBytes));
            }
        }

        var finalMerged = QuadtreeMerger.Merge(tracker.Flush(lastMs), grid, options.MaxAssetPixels);
        var (finalSprites, finalAnimations) = animationDetector.Process(finalMerged, options.TileSize);
        EmitRuns(finalSprites, doc, assetStore, mapping);
        EmitAnimations(finalAnimations, doc, assetStore, mapping);

        var (tailSprites, tailAnimations) = animationDetector.FlushAll();
        EmitRuns(tailSprites, doc, assetStore, mapping);
        EmitAnimations(tailAnimations, doc, assetStore, mapping);

        OsbWriter.Write(doc, options.OutputPath);
        OsbValidator.Validate(options.OutputPath, doc);

        stopwatch.Stop();

        var naive = await NaiveBaseline.EstimateAsync(options.InputPath, width, height, options.Start, options.Duration, frameCount, ct);

        return new EncodeStatistics
        {
            InputPath = options.InputPath,
            Width = width,
            Height = height,
            Fps = fps,
            Duration = effectiveDuration,
            FrameCount = frameCount,
            SpriteCount = doc.SpriteCount,
            AnimationCount = doc.AnimationCount,
            CommandCount = doc.CommandCount,
            AssetCount = assetStore.FileCount,
            AssetBytes = assetStore.TotalBytes,
            RawFrameBytes = (long)width * height * 3 * frameCount,
            NaiveEstimatedBytes = naive.EstimatedStoryboardBytes,
            OsbFileBytes = new FileInfo(options.OutputPath).Length,
            SourceFileBytes = new FileInfo(options.InputPath).Length,
            EncodeTime = stopwatch.Elapsed,
        };
    }

    private static void EmitRuns(List<TileRun> runs, SbDocument doc, AssetStore assetStore, CanvasMapping mapping)
    {
        foreach (var run in runs)
        {
            var (sbX, sbY) = mapping.PixelToStoryboard(run.PixelX, run.PixelY);
            var asset = assetStore.GetOrAdd(run.Rgb, run.Width, run.Height, AssetConsumer.Sprite);
            var scale = (float)mapping.StoryboardScale;

            doc.Add(new SbSprite
            {
                Layer = SbLayer.Background,
                Origin = SbOrigin.TopLeft,
                X = (float)sbX,
                Y = (float)sbY,
                Asset = asset,
                Commands =
                [
                    new SbValueCommand
                    {
                        Kind = SbCommandKind.Scale,
                        StartMs = run.StartMs,
                        EndMs = run.EndMs,
                        Start = scale,
                        End = scale,
                    },
                ],
            });
        }
    }

    private static void EmitAnimations(List<AnimationCandidate> candidates, SbDocument doc, AssetStore assetStore, CanvasMapping mapping)
    {
        foreach (var c in candidates)
        {
            var (sbX, sbY) = mapping.PixelToStoryboard(c.PixelX, c.PixelY);
            var basePath = assetStore.WriteAnimation(c.Frames, c.Width, c.Height);
            var scale = (float)mapping.StoryboardScale;

            doc.Add(new SbAnimation
            {
                Layer = SbLayer.Background,
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
                        Kind = SbCommandKind.Scale,
                        StartMs = c.StartMs,
                        EndMs = c.EndMs,
                        Start = scale,
                        End = scale,
                    },
                ],
            });
        }
    }
}
