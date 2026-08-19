using System.Diagnostics;
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
        var doc = new SbDocument();
        var mapping = new CanvasMapping(width, height);

        var loopOptions = new TileEncodeLoop.Options(
            options.InputPath, width, height, fps, options.Start, options.Duration,
            options.TileSize, options.HashQuantLevels, options.RawSnapshot, options.TileTolerance, options.Gop,
            options.MinAnimationUniqueness, options.NoQuadtree, options.MaxAssetPixels);

        var result = await TileEncodeLoop.RunAsync(loopOptions, doc, assetStore, mapping,
            (frame, pts) => onProgress?.Invoke(new EncodeProgress(frame, estimatedTotalFrames, pts, doc.SpriteCount, doc.CommandCount, assetStore.FileCount, assetStore.TotalBytes)),
            ct);
        var frameCount = result.FrameCount;

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
            AnimationFrameCount = assetStore.AnimationFrameCount,
            AssetBytes = assetStore.TotalBytes,
            RawFrameBytes = (long)width * height * 3 * frameCount,
            NaiveEstimatedBytes = naive.EstimatedStoryboardBytes,
            OsbFileBytes = new FileInfo(options.OutputPath).Length,
            SourceFileBytes = new FileInfo(options.InputPath).Length,
            EncodeTime = stopwatch.Elapsed,
        };
    }
}
