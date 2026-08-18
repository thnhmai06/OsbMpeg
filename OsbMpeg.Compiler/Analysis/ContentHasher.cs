using System.Security.Cryptography;

namespace OsbMpeg.Analysis;

/// <summary>Hashes tile pixel content after quantizing to `levels` per channel, so
/// near-identical tiles (film grain, gradient dither) collapse to the same hash instead of
/// oscillating between two hashes every frame.
///
/// ponytail: this is the whole hash-equality run test, no SAD/PSNR confirmation pass on top —
/// the design plan calls that out as the source of most naive-tile-codec failures on real
/// footage. Add a distortion check (compare each frame's tile against the run's representative
/// snapshot, threshold from --quality) if birdbrain_realword_fhd.mp4 benchmarks show runs
/// collapsing to length 1 and alternating between two hashes.</summary>
public static class ContentHasher
{
    public static ulong Hash(ReadOnlySpan<byte> rgb, int quantLevels)
    {
        Span<byte> quantized = rgb.Length <= 8192 ? stackalloc byte[rgb.Length] : new byte[rgb.Length];
        Quantize(rgb, quantized, quantLevels);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(quantized, digest);
        return BitConverter.ToUInt64(digest);
    }

    /// <summary>Same as <see cref="Hash(ReadOnlySpan{byte},int)"/>, but also writes the
    /// quantized bytes to <paramref name="canonical"/> so the caller can use them as the run's
    /// stored snapshot — two tiles that hash equal at this quantization level then also produce
    /// byte-identical snapshots, which is what lets AssetStore's content-hash dedupe actually
    /// fire across positions instead of only ever seeing raw, always-distinct pixels.</summary>
    public static ulong Hash(ReadOnlySpan<byte> rgb, int quantLevels, Span<byte> canonical)
    {
        Quantize(rgb, canonical, quantLevels);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(canonical, digest);
        return BitConverter.ToUInt64(digest);
    }

    private static void Quantize(ReadOnlySpan<byte> src, Span<byte> dst, int levels)
    {
        if (levels <= 0 || levels >= 256)
        {
            src.CopyTo(dst);
            return;
        }

        // bucket-center, not floor: floor drags every channel down by up to `step-1`
        // (mean step/2) — invisible while quantized bytes were hash-only scratch, but a
        // systematic darkening bias once they become the rendered snapshot (see canonical
        // snapshot mode in TileRunTracker).
        var step = 256 / levels;
        var half = step / 2;
        for (var i = 0; i < src.Length; i++)
            dst[i] = (byte)Math.Min(255, src[i] / step * step + half);
    }
}
