using OsbMpeg.Compiler.Encode;
using OsbMpeg.Parsers.Ir;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace OsbMpeg.Compiler.Tests;

/// <summary>
///     Spec: probe render bypasses the PNG round-trip via AssetStore.GetMemoryPixels. The whole
///     correctness argument rests on one claim — PNG encoding is lossless, so the stored
///     post-quantize pixels decode back to themselves. These tests pin that equivalence for both
///     quantized and unquantized sprites and for animation frame paths.
/// </summary>
public class AssetStoreMemoryPixelsTests
{
    private static byte[] Gradient(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < rgb.Length; i++)
            rgb[i] = (byte)(i * 7919 % 251 + 1); // deterministic, heavily textured — quantizes hard
        return rgb;
    }

    private static byte[] DecodeRgb24(byte[] pngBytes)
    {
        using var image = Image.Load<Rgb24>(pngBytes);
        var pixels = new byte[image.Width * image.Height * 3];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    [Fact]
    public void MemoryPixels_MatchDecodedPng_ForQuantizedSprite()
    {
        var store = new AssetStore("", "assets", "", pngCompressionLevel: 1, hexNaming: false, inMemory: true);
        var rgb = Gradient(64, 48);
        var id = store.GetOrAdd(rgb, 64, 48, AssetConsumer.Sprite, colors: 16);

        var stored = store.GetMemoryPixels(id);
        Assert.NotNull(stored);
        Assert.Equal((64, 48), (stored.Value.Width, stored.Value.Height));

        var fromPng = DecodeRgb24(store.GetMemoryBytes(id)!);
        Assert.Equal(fromPng, stored.Value.Pixels);
    }

    [Fact]
    public void MemoryPixels_MatchDecodedPng_ForColorsZero()
    {
        var store = new AssetStore("", "assets", "", pngCompressionLevel: 1, hexNaming: false, inMemory: true);
        var rgb = Gradient(32, 32);
        var id = store.GetOrAdd(rgb, 32, 32, AssetConsumer.Sprite, colors: 0);

        var stored = store.GetMemoryPixels(id);
        Assert.NotNull(stored);
        // no quantization: pixels are the exact input bytes, and the PNG decodes back to them
        Assert.Equal(rgb, stored.Value.Pixels);
        Assert.Equal(DecodeRgb24(store.GetMemoryBytes(id)!), stored.Value.Pixels);
    }

    [Fact]
    public void MemoryPixels_ArePostQuantize_NotRaw_WhenQuantizing()
    {
        var store = new AssetStore("", "assets", "", pngCompressionLevel: 1, hexNaming: false, inMemory: true);
        var rgb = Gradient(16, 16);
        var id = store.GetOrAdd(rgb, 16, 16, AssetConsumer.Sprite, colors: 2);

        var stored = store.GetMemoryPixels(id);
        Assert.NotNull(stored);
        // colors=2 palette must actually collapse the textured gradient — the capture point being
        // *after* quantize is load-bearing (capturing before would change PSNR).
        Assert.NotEqual(rgb, stored.Value.Pixels);
    }

    [Fact]
    public void MemoryPixels_KeyedByPerFramePath_ForAnimations()
    {
        var store = new AssetStore("", "assets", "", pngCompressionLevel: 1, hexNaming: true, inMemory: true);
        var frames = new[] { Gradient(24, 24), Gradient(24, 24) };
        var template = store.WriteAnimation(frames, 24, 24, colors: 8);

        Assert.Null(store.GetMemoryPixels(template)); // template path has no pixels — only frames do
        for (var i = 0; i < frames.Length; i++)
        {
            var frameId = new AssetId(template.RelativePath.Replace(".", $"{i}."));
            var stored = store.GetMemoryPixels(frameId);
            Assert.NotNull(stored);
            Assert.Equal(DecodeRgb24(store.GetMemoryBytes(frameId)!), stored.Value.Pixels);
        }
    }

    [Fact]
    public void MemoryPixels_Null_ForDiskBackedStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "osbmpeg_assetpixels_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AssetStore(dir, "assets", "t", hexNaming: true, inMemory: false);
            var id = store.GetOrAdd(Gradient(8, 8), 8, 8, AssetConsumer.Sprite, colors: 0);
            Assert.Null(store.GetMemoryPixels(id)); // disk path keeps the Image.Load round-trip
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}