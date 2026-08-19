using System.IO.Hashing;

namespace OsbMpeg.Compiler.Analysis;

/// <summary>
///     Hashes tile pixel content after quantizing to `levels` per channel, so
///     near-identical tiles (film grain, gradient dither) collapse to the same hash instead of
///     oscillating between two hashes every frame.
///     Uses XXH3-128 (non-cryptographic, no adversary in this pipeline — content only ever comes
///     from decoding the user's own video). 128 bits keeps accidental-collision odds negligible
///     for any realistic tile-run corpus, and it's both faster and wider than the SHA-256-truncated
///     -to-64-bits scheme this replaced. A hash *match* short-circuits the run-continuation check
///     below with zero confirmation (see TileRunTracker.Advance) — --tile-tolerance only guards
///     the opposite case (hash differs, pixels still close), so collision safety rests on the hash
///     width alone, not on that mechanism.
///     ponytail: this is the whole hash-equality run test, no SAD/PSNR confirmation pass on top —
///     the design plan calls that out as the source of most naive-tile-codec failures on real
///     footage. Add a distortion check (compare each frame's tile against the run's representative
///     snapshot, threshold from --quality) if birdbrain_realword_fhd.mp4 benchmarks show runs
///     collapsing to length 1 and alternating between two hashes.
/// </summary>
public static class ContentHasher
{
    public static UInt128 Hash(ReadOnlySpan<byte> rgb, int quantLevels)
    {
        var quantized = rgb.Length <= 8192 ? stackalloc byte[rgb.Length] : new byte[rgb.Length];
        Quantize(rgb, quantized, quantLevels);
        return XxHash128.HashToUInt128(quantized, 0);
    }

    /// <summary>
    ///     Same as <see cref="Hash(ReadOnlySpan{byte},int)" />, but also writes the
    ///     quantized bytes to <paramref name="canonical" /> so the caller can use them as the run's
    ///     stored snapshot — two tiles that hash equal at this quantization level then also produce
    ///     byte-identical snapshots, which is what lets AssetStore's content-hash dedupe actually
    ///     fire across positions instead of only ever seeing raw, always-distinct pixels.
    /// </summary>
    public static UInt128 Hash(ReadOnlySpan<byte> rgb, int quantLevels, Span<byte> canonical)
    {
        Quantize(rgb, canonical, quantLevels);
        return XxHash128.HashToUInt128(canonical, 0);
    }

    private static void Quantize(ReadOnlySpan<byte> src, Span<byte> dst, int levels)
    {
        if (levels is <= 0 or >= 256)
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