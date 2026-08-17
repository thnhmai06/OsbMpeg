using System.Security.Cryptography;
using OsbMpeg.Ir;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace OsbMpeg.Osb;

/// <summary>Which line type will reference the asset. osu!'s texture cache keys include
/// wrap mode (ClampToEdge for Sprite, None for Animation frames), so the same pixel content
/// used as both gets uploaded to the GPU twice — partition dedupe by consumer to reflect that
/// and never let a Sprite and an Animation frame share a file.</summary>
public enum AssetConsumer
{
    Sprite,
    AnimationFrame,
}

/// <summary>Content-hash addressed PNG asset store. File names are generated
/// (&lt;prefix&gt;&lt;n&gt;.png) so they always contain exactly one dot and no dot in the
/// directory component — required because osu!'s Animation frame path derivation
/// (Path.Replace(".", $"{i}.")) mangles any other dot in the path.</summary>
public sealed class AssetStore
{
    private readonly string _absoluteDir;
    private readonly string _relativeDir;
    private readonly string _namePrefix;
    private readonly int _colors;
    private readonly PngCompressionLevel _compressionLevel;
    private readonly Dictionary<(AssetConsumer Consumer, string Hash), AssetId> _dedupe = new();
    private int _counter;

    public int FileCount { get; private set; }
    public long TotalBytes { get; private set; }

    public AssetStore(string absoluteAssetDir, string relativeDirInOsb, string namePrefix, int colors = 0, int pngCompressionLevel = 6)
    {
        _absoluteDir = absoluteAssetDir;
        _relativeDir = relativeDirInOsb;
        _namePrefix = namePrefix;
        _colors = colors;
        _compressionLevel = (PngCompressionLevel)Math.Clamp(pngCompressionLevel, 0, 9);
        Directory.CreateDirectory(absoluteAssetDir);
    }

    /// <summary>rgb is packed Rgb24 (3 bytes/pixel, row-major, no padding) — the exact
    /// layout ffmpeg's rawvideo/rgb24 output uses.</summary>
    public AssetId GetOrAdd(ReadOnlySpan<byte> rgb, int width, int height, AssetConsumer consumer)
    {
        Span<byte> hashBytes = stackalloc byte[32];
        SHA256.HashData(rgb, hashBytes);
        var hash = Convert.ToHexString(hashBytes);
        var key = (consumer, hash);

        if (_dedupe.TryGetValue(key, out var existing))
            return existing;

        var fileName = $"{_namePrefix}{_counter++}.png";
        var absolute = Path.Combine(_absoluteDir, fileName);

        using (var image = Image.LoadPixelData<Rgb24>(rgb, width, height))
        {
            if (_colors > 0)
            {
                image.Mutate(ctx => ctx.Quantize(new OctreeQuantizer(new QuantizerOptions { MaxColors = _colors })));
            }

            image.SaveAsPng(absolute, new PngEncoder { CompressionLevel = _compressionLevel });
        }

        var id = new AssetId($"{_relativeDir}/{fileName}");
        _dedupe[key] = id;
        FileCount++;
        TotalBytes += new FileInfo(absolute).Length;
        return id;
    }
}
