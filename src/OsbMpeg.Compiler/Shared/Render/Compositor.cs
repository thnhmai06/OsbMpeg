namespace OsbMpeg.Compiler.Shared.Render;

/// <summary>Plain RGB24 framebuffer the renderer composites into.</summary>
public sealed class Canvas(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgb { get; } = new byte[width * height * 3];

    public void Clear(byte r, byte g, byte b)
    {
        for (var i = 0; i < Rgb.Length; i += 3)
        {
            Rgb[i] = r;
            Rgb[i + 1] = g;
            Rgb[i + 2] = b;
        }
    }
}

public readonly record struct SpriteFrame(byte[] Rgb, int Width, int Height);

/// <summary>
///     Blits a (possibly rotated/scaled/tinted) source image onto a destination canvas.
///     Straight (non-premultiplied) alpha, matching lazer's BlendingParameters: normal blend is
///     dst*(1-a) + src*a; additive is dst + src*a — verified against osu-framework's
///     BlendingParameters.Mixture/Additive struct literals (Source=SrcAlpha in both; Destination
///     is OneMinusSrcAlpha for normal, One for additive). Nearest-neighbor sampling — ponytail: no
///     bilinear filtering, add if reconstructed frames show visible aliasing on rotated/scaled tiles.
/// </summary>
public static class Compositor
{
    public static void Blit(
        Canvas dst, SpriteFrame src,
        double posX, double posY,
        double originFracX, double originFracY,
        double scaleX, double scaleY,
        double rotationRadians,
        double alpha,
        byte tintR, byte tintG, byte tintB,
        bool additive)
    {
        if (alpha <= 0 || scaleX == 0 || scaleY == 0 || src.Width == 0 || src.Height == 0)
            return;

        var pivotX = originFracX * src.Width;
        var pivotY = originFracY * src.Height;
        var cos = Math.Cos(rotationRadians);
        var sin = Math.Sin(rotationRadians);

        var c0 = Forward(0, 0);
        var c1 = Forward(src.Width, 0);
        var c2 = Forward(0, src.Height);
        var c3 = Forward(src.Width, src.Height);

        var minX = Math.Max(0, (int)Math.Floor(Min4(c0.X, c1.X, c2.X, c3.X)));
        var maxX = Math.Min(dst.Width - 1, (int)Math.Ceiling(Max4(c0.X, c1.X, c2.X, c3.X)));
        var minY = Math.Max(0, (int)Math.Floor(Min4(c0.Y, c1.Y, c2.Y, c3.Y)));
        var maxY = Math.Min(dst.Height - 1, (int)Math.Ceiling(Max4(c0.Y, c1.Y, c2.Y, c3.Y)));
        if (minX > maxX || minY > maxY)
            return;

        var invScaleX = 1.0 / scaleX;
        var invScaleY = 1.0 / scaleY;

        for (var py = minY; py <= maxY; py++)
        for (var px = minX; px <= maxX; px++)
        {
            var dx = px + 0.5 - posX;
            var dy = py + 0.5 - posY;
            // inverse rotation (R(-theta)) then inverse scale, back to source-local pixel space
            var rx = dx * cos + dy * sin;
            var ry = -dx * sin + dy * cos;
            var lx = rx * invScaleX + pivotX;
            var ly = ry * invScaleY + pivotY;

            var sxPix = (int)Math.Floor(lx);
            var syPix = (int)Math.Floor(ly);
            if (sxPix < 0 || sxPix >= src.Width || syPix < 0 || syPix >= src.Height)
                continue;

            var srcIdx = (syPix * src.Width + sxPix) * 3;
            var dstIdx = (py * dst.Width + px) * 3;

            var r = src.Rgb[srcIdx] * tintR / 255.0;
            var g = src.Rgb[srcIdx + 1] * tintG / 255.0;
            var b = src.Rgb[srcIdx + 2] * tintB / 255.0;

            if (additive)
            {
                dst.Rgb[dstIdx] = ClampByte(dst.Rgb[dstIdx] + r * alpha);
                dst.Rgb[dstIdx + 1] = ClampByte(dst.Rgb[dstIdx + 1] + g * alpha);
                dst.Rgb[dstIdx + 2] = ClampByte(dst.Rgb[dstIdx + 2] + b * alpha);
            }
            else
            {
                dst.Rgb[dstIdx] = ClampByte(dst.Rgb[dstIdx] * (1 - alpha) + r * alpha);
                dst.Rgb[dstIdx + 1] = ClampByte(dst.Rgb[dstIdx + 1] * (1 - alpha) + g * alpha);
                dst.Rgb[dstIdx + 2] = ClampByte(dst.Rgb[dstIdx + 2] * (1 - alpha) + b * alpha);
            }
        }

        return;

        (double X, double Y) Forward(double localX, double localY)
        {
            var sx = (localX - pivotX) * scaleX;
            var sy = (localY - pivotY) * scaleY;
            return (sx * cos - sy * sin + posX, sx * sin + sy * cos + posY);
        }
    }

    private static double Min4(double a, double b, double c, double d)
    {
        return Math.Min(Math.Min(a, b), Math.Min(c, d));
    }

    private static double Max4(double a, double b, double c, double d)
    {
        return Math.Max(Math.Max(a, b), Math.Max(c, d));
    }

    private static byte ClampByte(double v)
    {
        return (byte)Math.Clamp(v, 0, 255);
    }
}