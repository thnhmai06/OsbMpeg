namespace OsbMpeg.Ir;

/// <summary>osu! storyboard coordinate mapping. Storyboard-space is 640x480 nominal,
/// widescreen-extended and uniformly scaled to fill the actual render height — this mirrors
/// lazer's DrawableStoryboard math exactly (DrawScale = canvasHeight/480, layer width =
/// canvasHeight*16/9, centered over the 640-wide sprite coordinate box). The encoder uses
/// PixelToStoryboard when writing sprite positions; the renderer uses StoryboardToPixel to
/// reconstruct frames. When the renderer's canvas size matches the encoder's, the round trip
/// is exact — osu!'s own upscale cancels the encoder's downscale.</summary>
public readonly struct CanvasMapping
{
    /// <summary>Canvas pixels per storyboard unit (osu!'s DrawScale).</summary>
    public double ScaleToCanvas { get; }

    /// <summary>Storyboard units per canvas pixel — this is "k" in the design notes.</summary>
    public double StoryboardScale { get; }

    /// <summary>Storyboard-space x offset added before scaling, so that storyboard x=0
    /// lands at the left edge of the centered 640-wide box (negative when widescreen-extended).</summary>
    public double OffsetXStoryboard { get; }

    public CanvasMapping(int canvasWidth, int canvasHeight)
    {
        ScaleToCanvas = canvasHeight / 480.0;
        StoryboardScale = 480.0 / canvasHeight;
        var widescreenStoryboardWidth = canvasWidth / ScaleToCanvas;
        OffsetXStoryboard = (widescreenStoryboardWidth - 640.0) / 2.0;
    }

    public (double X, double Y) PixelToStoryboard(double pixelX, double pixelY)
        => (pixelX * StoryboardScale - OffsetXStoryboard, pixelY * StoryboardScale);

    public (double X, double Y) StoryboardToPixel(double sbX, double sbY)
        => ((sbX + OffsetXStoryboard) * ScaleToCanvas, sbY * ScaleToCanvas);
}
