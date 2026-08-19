using System.Globalization;

namespace OsbMpeg.Compiler.Encoder;

public sealed record EncodeOptions(
    string InputPath,
    string OutputPath,
    string AssetDir,
    string AssetRelativeDir,
    string AssetNamePrefix,
    int CanvasWidth,
    int CanvasHeight,
    double Fps,
    bool KeepSource,
    int TileSize,
    int HashQuantLevels,
    int Colors,
    int PngCompressionLevel,
    int Gop = 300,
    bool RawSnapshot = false,
    double MinAnimationUniqueness = 0.8,
    int TileTolerance = 0,
    bool NoQuadtree = false,
    TimeSpan? Start = null,
    TimeSpan? Duration = null,
    long MaxAssetPixels = 17_000_000)
{
    public static (int Width, int Height) ParseSize(string spec)
    {
        var parts = spec.Split('x', 'X');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) || w <= 0 ||
            h <= 0)
            throw new ArgumentException($"Invalid size '{spec}', expected WxH (e.g. 1920x1080).");
        return (w, h);
    }

    /// <summary>
    ///     ffmpeg -ss/-t accept either plain seconds ("12.5") or "[HH:]MM:SS[.ms]" —
    ///     TimeSpan.Parse alone would misread "10" as 10 days, not 10 seconds.
    /// </summary>
    public static TimeSpan? ParseFFmpegTime(string? value)
    {
        if (value is null)
            return null;
        return double.TryParse(value, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Parse(value, CultureInfo.InvariantCulture);
    }
}