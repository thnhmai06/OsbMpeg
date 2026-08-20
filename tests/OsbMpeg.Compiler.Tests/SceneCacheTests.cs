using OsbMpeg.Compiler.Tuning;
using Xunit;

namespace OsbMpeg.Compiler.Tests;

public class SceneCacheTests
{
    [Fact]
    public async Task NoCutsFound_ProducesOneSceneSpanningWholeDuration_Untuned()
    {
        var scenes = await SceneCache.BuildCoreAsync(10000,
            _ => Task.FromResult<IReadOnlyList<double>>([]),
            "test", null, CancellationToken.None);

        var scene = Assert.Single(scenes);
        Assert.Equal(0, scene.StartMs);
        Assert.Equal(10000, scene.EndMs);
        Assert.Null(scene.Tuned);
    }

    [Fact]
    public async Task CutsFound_ProduceOneSceneEachWithBoundariesOnly_NoTuning()
    {
        var scenes = await SceneCache.BuildCoreAsync(15000,
            _ => Task.FromResult<IReadOnlyList<double>>([5000, 10000]),
            "test", null, CancellationToken.None);

        Assert.Equal(3, scenes.Count);
        Assert.Equal((0.0, 5000.0), (scenes[0].StartMs, scenes[0].EndMs));
        Assert.Equal((5000.0, 10000.0), (scenes[1].StartMs, scenes[1].EndMs));
        Assert.Equal((10000.0, 15000.0), (scenes[2].StartMs, scenes[2].EndMs));
        // BuildCoreAsync is detection-only -- tuning is lazy (EnsureTunedAsync), so every scene
        // starts untuned regardless of how many were detected.
        Assert.All(scenes, s => Assert.Null(s.Tuned));
    }

    // BuildAsync (the real, non-injected entry point) and EnsureTunedAsync's untuned path both
    // call real decode/ParameterTuner, so they're covered by this feature's real end-to-end
    // verification (plan file), not a fast unit test here -- no scenes.json exists to fake a
    // cache hit against anymore (persistence was dropped on request: it left a side file next to
    // the real PNG assets a project ships, in exchange for cross-run reuse this tool's one-shot
    // usage pattern doesn't actually want).
}
