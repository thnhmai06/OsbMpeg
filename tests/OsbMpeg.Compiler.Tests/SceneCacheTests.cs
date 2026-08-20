using OsbMpeg.Compiler.Tuning;
using Xunit;

namespace OsbMpeg.Compiler.Tests;

public class SceneCacheTests
{
    private static readonly TunedParameters SomeCombo = new(128, 32, 8, 0);
    private static readonly TunedParameters OtherCombo = new(256, 16, 4, 16);

    [Fact]
    public async Task NoCutsFound_ProducesOneSceneSpanningWholeDuration_FirstSegmentTrue()
    {
        var tuneCalls = new List<(double Start, double End, bool IsFirst)>();

        var scenes = await SceneCache.BuildCoreAsync(10000,
            _ => Task.FromResult<IReadOnlyList<double>>([]),
            (start, end, isFirst, _) =>
            {
                tuneCalls.Add((start, end, isFirst));
                return Task.FromResult(SomeCombo);
            },
            "test", null, CancellationToken.None);

        var scene = Assert.Single(scenes);
        Assert.Equal(0, scene.StartMs);
        Assert.Equal(10000, scene.EndMs);
        Assert.Equal(SomeCombo, scene.Tuned);
        Assert.Equal([(0.0, 10000.0, true)], tuneCalls);
    }

    [Fact]
    public async Task CutsFound_ProduceOneSceneEach_OnlyFirstIsFirstSegment()
    {
        var tuneCalls = new List<(double Start, double End, bool IsFirst)>();

        var scenes = await SceneCache.BuildCoreAsync(15000,
            _ => Task.FromResult<IReadOnlyList<double>>([5000, 10000]),
            (start, end, isFirst, _) =>
            {
                tuneCalls.Add((start, end, isFirst));
                return Task.FromResult(isFirst ? SomeCombo : OtherCombo);
            },
            "test", null, CancellationToken.None);

        Assert.Equal(3, scenes.Count);
        Assert.Equal((0.0, 5000.0), (scenes[0].StartMs, scenes[0].EndMs));
        Assert.Equal((5000.0, 10000.0), (scenes[1].StartMs, scenes[1].EndMs));
        Assert.Equal((10000.0, 15000.0), (scenes[2].StartMs, scenes[2].EndMs));
        Assert.Equal(SomeCombo, scenes[0].Tuned);
        Assert.Equal(OtherCombo, scenes[1].Tuned);
        Assert.Equal(OtherCombo, scenes[2].Tuned);
        Assert.Equal([(0.0, 5000.0, true), (5000.0, 10000.0, false), (10000.0, 15000.0, false)], tuneCalls);
    }

    [Fact]
    public async Task LoadOrBuildAsync_CacheHit_ReadsBackWithoutCallingScanOrTune()
    {
        var dir = Path.Combine(Path.GetTempPath(), "osbmpeg_scenecache_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // DetectorVersion here must match SceneCache's own private const -- if they drift,
            // this "cache hit" test silently becomes a "cache miss, fall through to a real decode
            // of a nonexistent file" test instead (which hangs -- FrameSource's named-pipe reader
            // blocks forever if ffmpeg never opens the output pipe; see the comment at the bottom
            // of this file). Bump this literal whenever SceneCache.DetectorVersion changes.
            await File.WriteAllTextAsync(Path.Combine(dir, "scenes.json"),
                """{"DetectorVersion":3,"Scenes":[{"StartMs":0,"EndMs":5000,"TileSize":128,"HashQuantLevels":32,"TileTolerance":8,"Colors":0}]}""");

            var info = new OsbMpeg.Compiler.Media.MediaInfo(100, 100, 30, TimeSpan.FromSeconds(5), "h264");
            var scenes = await SceneCache.LoadOrBuildAsync(dir, "nonexistent.mp4", info, 30, null,
                CancellationToken.None);

            var scene = Assert.Single(scenes);
            Assert.Equal(new TunedParameters(128, 32, 8, 0), scene.Tuned);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // "Version mismatch triggers rebuild" (the miss path) needs a real decode to prove -- covered
    // by this feature's real end-to-end verification (plan file), not a fast unit test here. A
    // nonexistent-input-file attempt was tried and removed: FrameSource's named-pipe reader blocks
    // forever if ffmpeg never opens the output pipe at all (e.g. fails before producing output),
    // which isn't something to work around in a test -- it needs a real, decodable file either way.
}
