namespace OsbMpeg.Ir;

public readonly record struct SbColor(byte R, byte G, byte B)
{
    public static readonly SbColor White = new(255, 255, 255);
}
