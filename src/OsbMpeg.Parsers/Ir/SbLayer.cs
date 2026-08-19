namespace OsbMpeg.Parsers.Ir;

/// <summary>
///     osu! storyboard sprite layers, back to front: Background, Fail, Pass, Foreground, Overlay.
///     Video/Samples layers are not modeled — out of scope for this codec.
/// </summary>
public enum SbLayer : byte
{
    Background = 0,
    Fail = 1,
    Pass = 2,
    Foreground = 3,
    Overlay = 4
}