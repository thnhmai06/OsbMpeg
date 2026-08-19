using System.IO.Hashing;
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
///     Content-hash addressed PNG asset store. Two layouts, selected by
///     <paramref name="hexNaming" /> — see its own doc for which:
///     hexNaming layout (.osbv compile path): s/{hash}.png, a/{hash}/f{n}.png — {hash} is the
///     content's own XXH3-128 hex digest, not a counter, so the same tile content always resolves
///     to the same path across separate process invocations. That makes the asset dir a genuine
///     persistent cache: <see cref="SavePng" /> skips re-encoding whenever the target file already
///     exists, so re-running a compile over unchanged (or overlapping) footage reuses prior PNGs
///     instead of re-doing PNG encode work. Safe because a hash match only ever happens for the
///     same quantized bytes (128-bit XXH3, no adversary in this pipeline — see ContentHasher) —
///     never trust an existing file under the legacy counter layout below, where the same name can
///     legitimately mean different content across runs.
///     legacy layout (whole-canvas bench path): sprites/{prefix}{n}.png,
///     animations/a{id}/a{id}.png (+ per-frame a{id}{i}.png written by WriteAnimation) — a plain
///     per-process counter, one fresh output dir per invocation, never reused across runs.
///     Every Animation (either layout) gets its own subfolder so two different animations can
///     never collide on-disk: osu!'s frame path derivation (Path.Replace(".", $"{i}.")) operates
///     on the *whole* declared path, so distinct folders stay distinct after substitution even
///     when bare filenames could otherwise collide. Each animation's own base filename still has
///     exactly one dot and its own folder has none, matching osu!'s substitution rule.
///     ponytail: tried JPEG-for-opaque-tiles (osu-wiki's own advice: PNG for transparency, JPEG
///     otherwise) to cut asset bytes. Measured worse on both fixtures tested — +6.5% on
///     line-art anime, +62.6% on bad_apple's flat 2-color content — because JPEG's fixed
///     per-8x8-block cost and edge ringing lose to PNG's lossless deflate on exactly the kind of
///     content this codec's tiles tend to be (small, flat-color or line-art, heavily repeated).
///     Reverted; stick to PNG unless a fixture with real photographic gradients shows otherwise.
///     ponytail: no directory sharding (e.g. splitting s/{hash} into s/{hash[..2]}/{hash}) —
///     a flat dir with a huge file count can get slow on some filesystems, but that's only a
///     concern once one cache dir accumulates far more than a single encode's own asset count
///     (tens of thousands) across many runs. Add sharding if a long-lived shared cache dir
///     measurably shows it.
/// </summary>
public sealed class AssetStore
{
    private readonly string _absoluteDir;
    private readonly Dictionary<UInt128, AssetId> _animationDedupe = new();
    private readonly int _colors;
    private readonly PngCompressionLevel _compressionLevel;
    private readonly Dictionary<(AssetConsumer Consumer, UInt128 Hash), AssetId> _dedupe = new();
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
        var hash = XxHash128.HashToUInt128(rgb, 0);
        var key = (consumer, hash);

        if (_dedupe.TryGetValue(key, out var existing))
            return existing;

        var relativePath = _hexNaming ? $"s/{hash:x32}.png" : $"sprites/{_namePrefix}{_counter++}.png";
        var id = SavePng(relativePath, rgb, width, height);
        _dedupe[key] = id;
        return id;
    }

    /// <summary>
    ///     Writes an Animation's frame sequence into its own subfolder. The returned
    ///     AssetId is the base template path (one dot); frame i lives at
    ///     template.Replace(".", "{i}."), matching SbAnimation.FramePath.
    ///     hexNaming mode: the folder name is the XXH3-128 hash of the whole ordered frame
    ///     sequence (not any single frame — two animations sharing one frame's content but
    ///     differing elsewhere must never collide), so a repeated sequence dedupes both in-memory
    ///     (this run) and on-disk (across runs, via SavePng's cache-reuse) same as sprites.
    ///     legacy layout: not deduped — a frame's filename is positionally fixed by its
    ///     per-invocation counter, so there's nothing to look up.
    /// </summary>
    public AssetId WriteAnimation(IReadOnlyList<byte[]> frames, int width, int height)
    {
        string relDir;
        string baseFileName;
        UInt128? hash = null;

        if (_hexNaming)
        {
            var hasher = new XxHash128();
            foreach (var frame in frames)
                hasher.Append(frame);
            hash = hasher.GetCurrentHashAsUInt128();

            if (_animationDedupe.TryGetValue(hash.Value, out var cached))
                return cached;

            relDir = $"a/{hash.Value:x32}";
            baseFileName = "f.png";
        }
        else
        {
            relDir = $"animations/a{++_animationCounter}";
            baseFileName = $"a{_animationCounter}.png";
        }

        for (var i = 0; i < frames.Count; i++)
            SavePng($"{relDir}/{baseFileName.Replace(".", $"{i}.")}", frames[i], width, height);
        AnimationFrameCount += frames.Count;

        var id = new AssetId($"{_relativeDir}/{relDir}/{baseFileName}");
        if (hash is not null)
            _animationDedupe[hash.Value] = id;

        return id;
    }

    /// <param name="relativePath">
    ///     Forward-slash path relative to the asset dir; may include
    ///     subdirectories, which are created as needed.
    /// </param>
    private AssetId SavePng(string relativePath, ReadOnlySpan<byte> rgb, int width, int height)
    {
        var absolute = Path.Combine(_absoluteDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

        // hexNaming: the path is content-derived, so an existing file at this exact path is
        // guaranteed to already hold this exact content (from an earlier run) — skip re-encoding.
        // Never do this under the legacy counter layout, where the same path can legitimately
        // hold different content across separate invocations.
        if (!_hexNaming || !File.Exists(absolute))
        {
            var dir = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var image = Image.LoadPixelData<Rgb24>(rgb, width, height);
            if (_colors > 0)
                image.Mutate(ctx => ctx.Quantize(new OctreeQuantizer(new QuantizerOptions { MaxColors = _colors })));
            image.SaveAsPng(absolute, new PngEncoder { CompressionLevel = _compressionLevel });
        }

        FileCount++;
        TotalBytes += new FileInfo(absolute).Length;
        return new AssetId($"{_relativeDir}/{relativePath}");
    }
}