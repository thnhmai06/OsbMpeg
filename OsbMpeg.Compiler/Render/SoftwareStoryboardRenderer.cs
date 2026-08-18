using OsbMpeg.Ir;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace OsbMpeg.Render;

/// <summary>Renders a Storyboard IR document to RGB frames in-process, following the
/// semantics verified against ppy/osu (LegacyStoryboardDecoder / DrawableStoryboard*):
/// lifetime = [earliest command start, latest command end], values clamp to the nearest
/// command's edge outside their span, additive/normal blend per BlendingParameters, alpha
/// wraps modulo 1 above 1 (the flicker exploit lazer deliberately reproduces from stable).
///
/// Loop/Trigger children are honored for lifetime bounds but not for property evaluation —
/// the MVP tile-codec encoder never emits them, and OsbWriter still serializes them correctly
/// for real osu! to play back. ponytail: extend EvaluateScalar to walk into the active loop
/// iteration's children if/when an optimizer phase starts emitting SbLoop for real.</summary>
public sealed class SoftwareStoryboardRenderer
{
    private readonly string _assetRootDir;
    private readonly CanvasMapping _mapping;
    private readonly List<(SbObject Obj, double Start, double End)> _renderList;
    private readonly Dictionary<AssetId, SpriteFrame> _assetCache = new();

    public int Width { get; }
    public int Height { get; }

    public SoftwareStoryboardRenderer(SbDocument doc, string assetRootDir, int width, int height)
    {
        _assetRootDir = assetRootDir;
        Width = width;
        Height = height;
        _mapping = new CanvasMapping(width, height);

        _renderList = [];
        foreach (var layer in new[] { SbLayer.Background, SbLayer.Fail, SbLayer.Pass, SbLayer.Foreground, SbLayer.Overlay })
        {
            if (!doc.Layers.TryGetValue(layer, out var objects))
                continue;
            foreach (var obj in objects)
            {
                if (!obj.HasCommands)
                    continue; // never instantiated by the real renderer either
                var (start, end) = Lifetime(obj);
                _renderList.Add((obj, start, end));
            }
        }
    }

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

        var alpha = Flicker(EvaluateScalar(obj.Commands, SbCommandKind.Fade, t, 1f));
        var scaleUniform = EvaluateScalar(obj.Commands, SbCommandKind.Scale, t, 1f);
        var vectorScaleX = EvaluateScalar(obj.Commands, SbCommandKind.VectorScaleX, t, 1f);
        var vectorScaleY = EvaluateScalar(obj.Commands, SbCommandKind.VectorScaleY, t, 1f);
        var rotation = EvaluateScalar(obj.Commands, SbCommandKind.Rotate, t, 0f);
        var x = EvaluateScalar(obj.Commands, SbCommandKind.MoveX, t, obj.X);
        var y = EvaluateScalar(obj.Commands, SbCommandKind.MoveY, t, obj.Y);
        var (colR, colG, colB) = EvaluateColour(obj.Commands, t);
        var flipH = EvaluateFlag(obj.Commands, SbCommandKind.FlipH, t);
        var flipV = EvaluateFlag(obj.Commands, SbCommandKind.FlipV, t);
        var additive = EvaluateFlag(obj.Commands, SbCommandKind.Additive, t);

        var (px, py) = _mapping.StoryboardToPixel(x, y);
        var scaleX = scaleUniform * vectorScaleX * (flipH ? -1 : 1) * _mapping.ScaleToCanvas;
        var scaleY = scaleUniform * vectorScaleY * (flipV ? -1 : 1) * _mapping.ScaleToCanvas;
        var (originX, originY) = OriginFraction(obj.Origin);

        Compositor.Blit(canvas, frame, px, py, originX, originY, scaleX, scaleY, rotation, alpha, colR, colG, colB, additive);
    }

    private SpriteFrame GetAsset(SbObject obj, double t, double earliestStart) => obj switch
    {
        SbSprite s => LoadAsset(s.Asset),
        SbAnimation a => LoadAsset(PickAnimationFrame(a, t, earliestStart)),
        _ => default,
    };

    private static AssetId PickAnimationFrame(SbAnimation a, double t, double earliestStart)
    {
        var index = (int)((t - earliestStart) / a.FrameDelayMs);
        index = a.LoopType == SbLoopType.LoopForever
            ? ((index % a.FrameCount) + a.FrameCount) % a.FrameCount
            : Math.Clamp(index, 0, a.FrameCount - 1);
        return a.FramePath(index);
    }

    private SpriteFrame LoadAsset(AssetId id)
    {
        if (_assetCache.TryGetValue(id, out var cached))
            return cached;

        var path = Path.Combine(_assetRootDir, id.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        using var image = Image.Load<Rgb24>(path);
        var buffer = new byte[image.Width * image.Height * 3];
        image.CopyPixelDataTo(buffer);
        var frame = new SpriteFrame(buffer, image.Width, image.Height);
        _assetCache[id] = frame;
        return frame;
    }

    /// <summary>lazer reproduces a stable exploit where alpha values above 1 wrap instead of
    /// clamping, used deliberately by storyboarders for flicker effects.</summary>
    private static float Flicker(float alpha) => alpha > 1 ? alpha % 1 : alpha;

    private static (double X, double Y) OriginFraction(SbOrigin origin) => origin switch
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
        _ => (0, 0),
    };

    /// <summary>Active span: earliest start to latest end across all non-trigger top-level
    /// commands (matches EarliestTransformTime / EndTimeForDisplay). Loop spans use the loop's
    /// own start plus (max relative child end) * effective iteration count, min 1 iteration.</summary>
    private static (double Start, double End) Lifetime(SbObject obj)
    {
        var start = double.MaxValue;
        var end = double.MinValue;

        foreach (var c in obj.Commands)
        {
            if (c is SbTrigger)
                continue;

            var (s, e) = c is SbLoop loop ? LoopSpan(loop) : (c.StartMs, c.EndMs);
            start = Math.Min(start, s);
            end = Math.Max(end, e);
        }

        return start == double.MaxValue ? (0, 0) : (start, end);
    }

    private static (double Start, double End) LoopSpan(SbLoop loop)
    {
        var childEnd = 0.0;
        foreach (var c in loop.Children)
        {
            if (c is SbTrigger)
                continue;
            childEnd = Math.Max(childEnd, c.EndMs);
        }

        var iterations = Math.Max(0, loop.Count - 1) + 1;
        return (loop.StartMs, loop.StartMs + childEnd * iterations);
    }

    private static float EvaluateScalar(List<SbCommand> commands, SbCommandKind kind, double t, float defaultValue)
    {
        SbValueCommand? active = null;
        SbValueCommand? last = null;
        SbValueCommand? first = null;

        foreach (var c in commands)
        {
            if (c is not SbValueCommand v || v.Kind != kind)
                continue;
            first ??= v;
            if (t >= v.StartMs && t <= v.EndMs)
                active = v;
            if (last is null || v.EndMs >= last.EndMs)
                last = v;
        }

        if (first is null)
            return defaultValue;
        if (active is not null)
        {
            if (active.StartMs == active.EndMs)
                return active.End;
            var p = (t - active.StartMs) / (active.EndMs - active.StartMs);
            return Lerp(active.Start, active.End, (float)EasingTable.Apply(active.Easing, p));
        }

        return t < first.StartMs ? first.Start : last!.End;
    }

    private static (byte R, byte G, byte B) EvaluateColour(List<SbCommand> commands, double t)
    {
        SbColourCommand? active = null;
        SbColourCommand? last = null;
        SbColourCommand? first = null;

        foreach (var c in commands)
        {
            if (c is not SbColourCommand v)
                continue;
            first ??= v;
            if (t >= v.StartMs && t <= v.EndMs)
                active = v;
            if (last is null || v.EndMs >= last.EndMs)
                last = v;
        }

        if (first is null)
            return (SbColor.White.R, SbColor.White.G, SbColor.White.B);

        if (active is not null)
        {
            if (active.StartMs == active.EndMs)
                return (active.End.R, active.End.G, active.End.B);
            var p = (float)EasingTable.Apply(active.Easing, (t - active.StartMs) / (active.EndMs - active.StartMs));
            return (
                (byte)Lerp(active.Start.R, active.End.R, p),
                (byte)Lerp(active.Start.G, active.End.G, p),
                (byte)Lerp(active.Start.B, active.End.B, p));
        }

        var c2 = t < first.StartMs ? first.Start : last!.End;
        return (c2.R, c2.G, c2.B);
    }

    /// <summary>P command semantics: StartMs==EndMs makes it permanent from that point on
    /// (never reverts); otherwise it is only active during [StartMs,EndMs).</summary>
    private static bool EvaluateFlag(List<SbCommand> commands, SbCommandKind kind, double t)
    {
        foreach (var c in commands)
        {
            if (c is not SbFlagCommand f || f.Kind != kind)
                continue;
            if (f.StartMs == f.EndMs ? t >= f.StartMs : t >= f.StartMs && t < f.EndMs)
                return true;
        }
        return false;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
