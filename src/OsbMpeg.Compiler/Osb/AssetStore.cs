using System.Security.Cryptography;
using OsbMpeg.Parsers.Ir;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace OsbMpeg.Compiler.Osb;

/// <summary>
///     Which line type will reference the asset. osu!'s texture cache keys include
///     wrap mode (ClampToEdge for Sprite, None for Animation frames), so the same pixel content
///     used as both gets uploaded to the GPU twice — partition dedupe by consumer to reflect that
///     and never let a Sprite and an Animation frame share a file.
/// </summary>
public enum AssetConsumer : byte
{
    Sprite,
    AnimationFrame
}

/// <summary>
///     Content-hash addressed PNG asset store, laid out as:
///     sprites/{prefix}{n}.png
///     animations/a{id}/a{id}.png (+ per-frame a{id}{i}.png written by WriteAnimation)
///     Every Animation gets its own numbered subfolder so two different animations can never
///     collide on-disk: osu!'s frame path derivation (Path.Replace(".", $"{i}.")) operates on the
///     *whole* declared path, so "animations/a7/a7.png" and "animations/a71/a71.png" stay distinct
///     after substitution even though the bare filenames "a7"+"10" and "a71"+"0" would otherwise
///     both stringify to "a710" — a real collision that silently overwrote whichever animation's
///     frame lost the race (fixed here, previously fixed with zero-padding instead — folder
///     namespacing is strictly better since it doesn't depend on an assumed max counter width).
///     Each animation's own base filename still has exactly one dot and its own folder has none,
///     matching osu!'s substitution rule.
///     ponytail: tried JPEG-for-opaque-tiles (osu-wiki's own advice: PNG for transparency, JPEG
///     otherwise) to cut asset bytes. Measured worse on both fixtures tested — +6.5% on
///     line-art anime, +62.6% on bad_apple's flat 2-color content — because JPEG's fixed
///     per-8x8-block cost and edge ringing lose to PNG's lossless deflate on exactly the kind of
///     content this codec's tiles tend to be (small, flat-color or line-art, heavily repeated).
///     Reverted; stick to PNG unless a fixture with real photographic gradients shows otherwise.
/// </summary>
public sealed class AssetStore
{
    private readonly string _absoluteDir;
    private readonly int _colors;
    private readonly PngCompressionLevel _compressionLevel;
    private readonly Dictionary<(AssetConsumer Consumer, string Hash), AssetId> _dedupe = new();
    private readonly bool _hexNaming;
    private readonly string _namePrefix;
    private readonly string _relativeDir;
    private int _animationCounter;
    private int _counter;

    /// <param name="hexNaming">
    ///     Switches to the .osbv compiler's layout —
    ///     s/&lt;hex&gt;.png and a/&lt;hex&gt;/f&lt;n&gt;.png, both 0-based — instead of the
    ///     legacy sprites/{prefix}{n}.png / animations/a{id}/a{id}{n}.png layout the old
    ///     whole-canvas CLI still writes and validates against.
    /// </param>
    public AssetStore(string absoluteAssetDir, string relativeDirInOsb, string namePrefix, int colors = 0,
        int pngCompressionLevel = 6, bool hexNaming = false)
    {
        _absoluteDir = absoluteAssetDir;
        _relativeDir = relativeDirInOsb;
        _namePrefix = namePrefix;
        _colors = colors;
        _compressionLevel = (PngCompressionLevel)Math.Clamp(pngCompressionLevel, 0, 9);
        _hexNaming = hexNaming;
        Directory.CreateDirectory(absoluteAssetDir);
    }

    public int FileCount { get; private set; }
    public int AnimationFrameCount { get; private set; }
    public long TotalBytes { get; private set; }

    /// <summary>
    ///     rgb is packed Rgb24 (3 bytes/pixel, row-major, no padding) — the exact
    ///     layout ffmpeg's rawvideo/rgb24 output uses.
    /// </summary>
    public AssetId GetOrAdd(ReadOnlySpan<byte> rgb, int width, int height, AssetConsumer consumer)
    {
        Span<byte> hashBytes = stackalloc byte[32];
        SHA256.HashData(rgb, hashBytes);
        var hash = Convert.ToHexString(hashBytes);
        var key = (consumer, hash);

        if (_dedupe.TryGetValue(key, out var existing))
            return existing;

        var relativePath = _hexNaming ? $"s/{_counter++:x}.png" : $"sprites/{_namePrefix}{_counter++}.png";
        var id = SavePng(relativePath, rgb, width, height);
        _dedupe[key] = id;
        return id;
    }

    /// <summary>
    ///     Writes an Animation's frame sequence into its own numbered subfolder. The
    ///     returned AssetId is the base template path (one dot); frame i lives at
    ///     template.Replace(".", "{i}."), matching SbAnimation.FramePath. Not content-hash
    ///     deduped: Animation frames forfeit cross-tile dedupe by construction (a frame's filename
    ///     is positionally fixed by its index, it can't alias another file), so there's nothing to
    ///     look up.
    /// </summary>
    public AssetId WriteAnimation(IReadOnlyList<byte[]> frames, int width, int height)
    {
        var relDir = _hexNaming ? $"a/{_animationCounter++:x}" : $"animations/a{++_animationCounter}";
        var baseFileName = _hexNaming ? "f.png" : $"a{_animationCounter}.png";

        for (var i = 0; i < frames.Count; i++)
            SavePng($"{relDir}/{baseFileName.Replace(".", $"{i}.")}", frames[i], width, height);
        AnimationFrameCount += frames.Count;

        return new AssetId($"{_relativeDir}/{relDir}/{baseFileName}");
    }

    /// <param name="relativePath">
    ///     Forward-slash path relative to the asset dir; may include
    ///     subdirectories, which are created as needed.
    /// </param>
    private AssetId SavePng(string relativePath, ReadOnlySpan<byte> rgb, int width, int height)
    {
        var absolute = Path.Combine(_absoluteDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using (var image = Image.LoadPixelData<Rgb24>(rgb, width, height))
        {
            if (_colors > 0)
                image.Mutate(ctx => ctx.Quantize(new OctreeQuantizer(new QuantizerOptions { MaxColors = _colors })));
            image.SaveAsPng(absolute, new PngEncoder { CompressionLevel = _compressionLevel });
        }

        FileCount++;
        TotalBytes += new FileInfo(absolute).Length;
        return new AssetId($"{_relativeDir}/{relativePath}");
    }
}