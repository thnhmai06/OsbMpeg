namespace OsbMpeg.Parsers.Ir;

/// <summary>
///     osu! storyboard coordinate mapping. Storyboard-space is 640x480 nominal,
///     widescreen-extended and uniformly scaled to fill the actual render height — this mirrors
///     lazer's DrawableStoryboard math exactly (DrawScale = canvasHeight/480, layer width =
///     canvasHeight*16/9, centered over the 640-wide sprite coordinate box). The encoder uses
///     PixelToStoryboard when writing sprite positions; the renderer uses StoryboardToPixel to
///     reconstruct frames. When the renderer's canvas size matches the encoder's, the round trip
///     is exact — osu!'s own upscale cancels the encoder's downscale.
/// </summary>
public readonly struct CanvasMapping
{
    /// <summary>Canvas pixels per storyboard unit (osu!'s DrawScale).</summary>
    public double ScaleToCanvas { get; }

    /// <summary>Storyboard units per canvas pixel — this is "k" in the design notes.</summary>
    public double StoryboardScale { get; }

    /// <summary>
    ///     Storyboard-space x offset added before scaling, so that storyboard x=0
    ///     lands at the left edge of the centered 640-wide box (negative when widescreen-extended).
    /// </summary>
    public double OffsetXStoryboard { get; }

    /// <summary>
    ///     Same as OffsetXStoryboard but for y. Always 0 for the whole-canvas
    ///     constructor (the canvas top always maps to storyboard y=0); nonzero only for the
    ///     arbitrary-placement constructor below.
    /// </summary>
    public double OffsetYStoryboard { get; }

    public CanvasMapping(int canvasWidth, int canvasHeight)
    {
        ScaleToCanvas = canvasHeight / 480.0;
        StoryboardScale = 480.0 / canvasHeight;
        var widescreenStoryboardWidth = canvasWidth / ScaleToCanvas;
        OffsetXStoryboard = (widescreenStoryboardWidth - 640.0) / 2.0;
        OffsetYStoryboard = 0;
    }

    /// <summary>
    ///     Auto-cover placement for a single video object (the .osbv compiler's default,
    ///     no-group-transform case): pixel space (0..pixelWidth, 0..pixelHeight) is scaled
    ///     uniformly by max(640/pixelWidth, 480/pixelHeight) — covering the legacy 640x480 box on
    ///     both axes, cropped by osu!'s own layer masking where it overflows — and centered so the
    ///     video's own center lands at (centerXStoryboard, centerYStoryboard).
    /// </summary>
    public CanvasMapping(int pixelWidth, int pixelHeight, double centerXStoryboard, double centerYStoryboard)
    {
        var scale = Math.Max(640.0 / pixelWidth, 480.0 / pixelHeight);
        StoryboardScale = scale;
        ScaleToCanvas = 1.0 / scale;
        OffsetXStoryboard = pixelWidth / 2.0 * scale - centerXStoryboard;
        OffsetYStoryboard = pixelHeight / 2.0 * scale - centerYStoryboard;
    }

    public (double X, double Y) PixelToStoryboard(double pixelX, double pixelY)
    {
        return (pixelX * StoryboardScale - OffsetXStoryboard, pixelY * StoryboardScale - OffsetYStoryboard);
    }

    public (double X, double Y) StoryboardToPixel(double sbX, double sbY)
    {
        return ((sbX + OffsetXStoryboard) * ScaleToCanvas, (sbY + OffsetYStoryboard) * ScaleToCanvas);
    }
}