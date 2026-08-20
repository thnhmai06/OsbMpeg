using OsbMpeg.Compiler.Encode;
using OsbMpeg.Parsers.Ir;
using OsbMpeg.Parsers.Render;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace OsbMpeg.Compiler.Shared.Render;

/// <summary>
///     Renders a Storyboard IR document to RGB frames in-process, following the
///     semantics verified against ppy/osu (LegacyStoryboardDecoder / DrawableStoryboard*):
///     lifetime = [earliest command start, latest command end], values clamp to the nearest
///     command's edge outside their span, additive/normal blend per BlendingParameters, alpha
///     wraps modulo 1 above 1 (the flicker exploit lazer deliberately reproduces from stable).
///     Loop/Trigger children are honored for lifetime bounds but not for property evaluation —
///     the MVP tile-codec encoder never emits them, and OsbWriter still serializes them correctly
///     for real osu! to play back. ponytail: extend EvaluateScalar to walk into the active loop
///     iteration's children if/when an optimizer phase starts emitting SbLoop for real.
/// </summary>
public sealed class SoftwareStoryboardRenderer
{
    private readonly Dictionary<AssetId, SpriteFrame> _assetCache = new();
    private readonly string _assetRootDir;
    private readonly AssetStore? _assetStore;
    private readonly CanvasMapping _mapping;
    private readonly List<(SbObject Obj, double Start, double End)> _renderList;

    /// <param name="assetStore">
    ///     Optional — when the SbDocument's own assets were written via an in-memory AssetStore
    ///     (see AssetStore.cs's <c>inMemory</c> doc comment; ParameterTuner.ProbeAsync's only
    ///     current caller), pass it here so LoadAsset reads those bytes back directly instead of
    ///     opening a file at <paramref name="assetRootDir" /> that was never written. Every other
    ///     caller (DecodeCommand, BenchCommand) reads real, disk-backed assets and leaves this null.
    /// </param>
    public SoftwareStoryboardRenderer(SbDocument doc, string assetRootDir, int width, int height,
        AssetStore? assetStore = null)
    {
        _assetRootDir = assetRootDir;
        _assetStore = assetStore;
        Width = width;
        Height = height;
        _mapping = new CanvasMapping(width, height);

        _renderList = [];
        foreach (var layer in new[]
                     { SbLayer.Background, SbLayer.Fail, SbLayer.Pass, SbLayer.Foreground, SbLayer.Overlay })
        {
            if (!doc.Layers.TryGetValue(layer, out var objects))
                continue;
            foreach (var obj in objects.Where(obj => obj.HasCommands))
            {
                var (start, end) = CommandEvaluator.Lifetime(obj.Commands);
                _renderList.Add((obj, start, end));
            }
        }
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Latest lifetime end across every object — the moment nothing is left to draw.</summary>
    public double DurationMs => _renderList.Count == 0 ? 0 : _renderList.Max(r => r.End);

    public Canvas RenderFrame(double timeMs)
    {
        var canvas = new Canvas(Width, Height);
        canvas.Clear(0, 0, 0);

        foreach (var (obj, start, end) in _renderList)
        {
            if (timeMs < start || timeMs > end)
                continue;
            RenderObject(canvas, obj, timeMs, start);
        }

        return canvas;
    }

    private void RenderObject(Canvas canvas, SbObject obj, double t, double earliestStart)
    {
        var frame = GetAsset(obj, t, earliestStart);
        if (frame.Width == 0 || frame.Height == 0)
            return;

        var alpha = CommandEvaluator.Flicker(CommandEvaluator.EvaluateScalar(obj.Commands, SbCommandKind.Fade, t, 1f));
        var scaleUniform = CommandEvaluator.EvaluateScalar(obj.Commands, SbCommandKind.Scale, t, 1f);
        var vectorScaleX = CommandEvaluator.EvaluateScalar(obj.Commands, SbCommandKind.VectorScaleX, t, 1f);
        var vectorScaleY = CommandEvaluator.EvaluateScalar(obj.Commands, SbCommandKind.VectorScaleY, t, 1f);
        var rotation = CommandEvaluator.EvaluateScalar(obj.Commands, SbCommandKind.Rotate, t, 0f);
        var x = CommandEvaluator.EvaluateScalar(obj.Commands, SbCommandKind.MoveX, t, obj.X);
        var y = CommandEvaluator.EvaluateScalar(obj.Commands, SbCommandKind.MoveY, t, obj.Y);
        var (colR, colG, colB) = CommandEvaluator.EvaluateColour(obj.Commands, t);
        var flipH = CommandEvaluator.EvaluateFlag(obj.Commands, SbCommandKind.FlipH, t);
        var flipV = CommandEvaluator.EvaluateFlag(obj.Commands, SbCommandKind.FlipV, t);
        var additive = CommandEvaluator.EvaluateFlag(obj.Commands, SbCommandKind.Additive, t);

        var (px, py) = _mapping.StoryboardToPixel(x, y);
        var scaleX = scaleUniform * vectorScaleX * (flipH ? -1 : 1) * _mapping.ScaleToCanvas;
        var scaleY = scaleUniform * vectorScaleY * (flipV ? -1 : 1) * _mapping.ScaleToCanvas;
        var (originX, originY) = OriginFraction(obj.Origin);

        Compositor.Blit(canvas, frame, px, py, originX, originY, scaleX, scaleY, rotation, alpha, colR, colG, colB,
            additive);
    }

    private SpriteFrame GetAsset(SbObject obj, double t, double earliestStart)
    {
        return obj switch
        {
            SbSprite s => LoadAsset(s.Asset),
            SbAnimation a => LoadAsset(PickAnimationFrame(a, t, earliestStart)),
            _ => default
        };
    }

    private static AssetId PickAnimationFrame(SbAnimation a, double t, double earliestStart)
    {
        var index = (int)((t - earliestStart) / a.FrameDelayMs);
        index = a.LoopType == SbLoopType.LoopForever
            ? (index % a.FrameCount + a.FrameCount) % a.FrameCount
            : Math.Clamp(index, 0, a.FrameCount - 1);
        return a.FramePath(index);
    }

    private SpriteFrame LoadAsset(AssetId id)
    {
        if (_assetCache.TryGetValue(id, out var cached))
            return cached;

        // Probe-renderer shortcut (AssetStore in-memory mode only; disk-backed stores return
        // null here): PNG is lossless, so the store's post-quantize pixels are exactly what
        // decoding its saved PNG would yield — skip the Image.Load round-trip entirely. This is
        // the probe path's biggest single render cost on churn-heavy content (Surface, since the
        // tuner's in-memory store keeps every unique tile's encoded bytes).
        if (_assetStore?.GetMemoryPixels(id) is { } stored)
        {
            var frame = new SpriteFrame(stored.Pixels, stored.Width, stored.Height);
            _assetCache[id] = frame;
            return frame;
        }

        using var image = _assetStore?.GetMemoryBytes(id) is { } bytes
            ? Image.Load<Rgb24>(bytes)
            : Image.Load<Rgb24>(Path.Combine(_assetRootDir, id.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var buffer = new byte[image.Width * image.Height * 3];
        image.CopyPixelDataTo(buffer);
        var loaded = new SpriteFrame(buffer, image.Width, image.Height);
        _assetCache[id] = loaded;
        return loaded;
    }

    private static (double X, double Y) OriginFraction(SbOrigin origin)
    {
        return origin switch
        {
            SbOrigin.TopLeft => (0, 0),
            SbOrigin.Centre => (0.5, 0.5),
            SbOrigin.CentreLeft => (0, 0.5),
            SbOrigin.TopRight => (1, 0),
            SbOrigin.BottomCentre => (0.5, 1),
            SbOrigin.TopCentre => (0.5, 0),
            SbOrigin.Custom => (0, 0), // falls through to TopLeft in lazer's parseOrigin switch
            SbOrigin.CentreRight => (1, 0.5),
            SbOrigin.BottomLeft => (0, 1),
            SbOrigin.BottomRight => (1, 1),
            _ => (0, 0)
        };
    }
}