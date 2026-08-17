namespace OsbMpeg.Coding;

/// <summary>Quality metrics for benchmark reconstruction comparison. Both operate on
/// equal-length packed Rgb24 buffers.</summary>
public static class Metrics
{
    public static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        double sumSquares = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sumSquares += d * d;
        }

        var mse = sumSquares / a.Length;
        return mse <= 0 ? 100.0 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>ponytail: global (whole-frame) SSIM — one mean/variance/covariance per frame,
    /// not the standard 11x11 sliding Gaussian window. Cheaper and structurally less sensitive
    /// to localized artifacts (a single wrong tile in an otherwise perfect frame barely moves
    /// this number). Swap in a windowed implementation if per-frame SSIM needs to catch that.</summary>
    public static double Ssim(byte[] lumaA, byte[] lumaB)
    {
        var n = lumaA.Length;
        double meanA = 0, meanB = 0;
        for (var i = 0; i < n; i++)
        {
            meanA += lumaA[i];
            meanB += lumaB[i];
        }
        meanA /= n;
        meanB /= n;

        double varA = 0, varB = 0, covAB = 0;
        for (var i = 0; i < n; i++)
        {
            var da = lumaA[i] - meanA;
            var db = lumaB[i] - meanB;
            varA += da * da;
            varB += db * db;
            covAB += da * db;
        }
        varA /= n;
        varB /= n;
        covAB /= n;

        const double c1 = 6.5025; // (0.01*255)^2
        const double c2 = 58.5225; // (0.03*255)^2
        return (2 * meanA * meanB + c1) * (2 * covAB + c2)
            / ((meanA * meanA + meanB * meanB + c1) * (varA + varB + c2));
    }

    public static byte[] ToLuma(ReadOnlySpan<byte> rgb)
    {
        var luma = new byte[rgb.Length / 3];
        for (int i = 0, j = 0; i < rgb.Length; i += 3, j++)
            luma[j] = (byte)(0.299 * rgb[i] + 0.587 * rgb[i + 1] + 0.114 * rgb[i + 2]);
        return luma;
    }
}
