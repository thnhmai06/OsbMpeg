using OsbMpeg.Compiler.Tuning;
using Xunit;

namespace OsbMpeg.Compiler.Tests;

public class ParameterTunerTests
{
    private const double BaselinePsnr = 30.0;

    private static Task<(ParameterTuner.ProbeResult Train, ParameterTuner.ProbeResult Eval)> Same(
        ParameterTuner.ProbeResult result) =>
        Task.FromResult((result, result));

    [Fact]
    public async Task StaysAtBaseline_WhenNoCandidateIsCheaper()
    {
        var probeCount = 0;

        var tuned = await ParameterTuner.TuneCoreAsync(Probe, "test", null);

        Assert.Equal(new TunedParameters(64, 32, 8, 0), tuned);
        // 3+3+3+4=13 candidates minus 3 seeded (each axis's "unchanged" candidate reuses the
        // previous axis's own winning result instead of re-probing) minus baseline folded into
        // the TileSize axis's own batch (its 64 candidate IS the baseline tuple) = 10.
        Assert.Equal(10, probeCount);
        return;

        Task<(ParameterTuner.ProbeResult, ParameterTuner.ProbeResult)> Probe(int tileSize, int hashQuant,
            int tolerance, int colors)
        {
            probeCount++;
            var isBaseline = tileSize == 64 && hashQuant == 32 && tolerance == 8 && colors == 0;
            // every non-baseline candidate is both worse AND more expensive -- nothing should win
            return Same(isBaseline
                ? new ParameterTuner.ProbeResult(BaselinePsnr, 100_000, 500)
                : new ParameterTuner.ProbeResult(BaselinePsnr - 5, 200_000, 500));
        }
    }

    [Fact]
    public async Task PicksCheaperCandidate_WhenItStaysWithinSlackOfFloor()
    {
        var tuned = await ParameterTuner.TuneCoreAsync(Probe, "test", null);

        Assert.Equal(16, tuned.Colors);
        return;

        Task<(ParameterTuner.ProbeResult, ParameterTuner.ProbeResult)> Probe(int tileSize, int hashQuant,
            int tolerance, int colors)
        {
            // Colors=16 costs half as much and stays within the 1dB slack -- should win its axis.
            if (tileSize == 64 && hashQuant == 32 && tolerance == 8 && colors == 16)
                return Same(new ParameterTuner.ProbeResult(BaselinePsnr - 0.5, 50_000, 500));

            var isBaseline = tileSize == 64 && hashQuant == 32 && tolerance == 8 && colors == 0;
            return Same(isBaseline
                ? new ParameterTuner.ProbeResult(BaselinePsnr, 100_000, 500)
                : new ParameterTuner.ProbeResult(BaselinePsnr - 5, 200_000, 500));
        }
    }

    [Fact]
    public async Task LastAxisSeedIsCarriedForward_NotReProbed_WhenItsFreshCandidatesAreAllWorse()
    {
        // Floor is baseline.Psnr - slack, so baseline always trivially meets its own floor --
        // and every axis carries its winner's ProbeResult into the next axis as the seed for
        // the "unchanged" candidate, so that seed is guaranteed floor-passing too, all the way
        // through. With deterministic probes (the only kind the real ProbeAsync produces),
        // TuneCoreAsync's "no combo met the floor" fallback is therefore structurally
        // unreachable -- what's left to verify is that the last axis's fresh candidates being
        // uniformly worse doesn't wrongly override the still-valid seeded/unchanged value.
        var probeCount = 0;

        var tuned = await ParameterTuner.TuneCoreAsync(Probe, "test", null);

        Assert.Equal(new TunedParameters(64, 32, 8, 0), tuned);
        Assert.Equal(10, probeCount); // no extra re-probe of the seeded (64,32,8,0) tuple anywhere
        return;

        Task<(ParameterTuner.ProbeResult, ParameterTuner.ProbeResult)> Probe(int tileSize, int hashQuant,
            int tolerance, int colors)
        {
            probeCount++;
            var isBaseline = tileSize == 64 && hashQuant == 32 && tolerance == 8 && colors == 0;
            return Same(isBaseline
                ? new ParameterTuner.ProbeResult(BaselinePsnr, 100_000, 500)
                : new ParameterTuner.ProbeResult(BaselinePsnr - 50, 200_000, 500));
        }
    }

    [Fact]
    public async Task RejectsCandidate_WhenItOverfitsTrain_AndEvalPsnrMisses_TheFloor()
    {
        var tuned = await ParameterTuner.TuneCoreAsync(Probe, "test", null);

        Assert.Equal(0, tuned.Colors); // stayed at baseline -- the overfit candidate got rejected
        return;

        // Colors=16 looks like a clean win on train (cheap, within slack) -- but its eval PSNR
        // craters well below the floor, the exact "looked great on the sample it was tuned
        // against, falls apart on unseen material" case the train/eval split exists to catch.
        // It must lose its axis to the baseline despite being cheaper and train-floor-passing.
        Task<(ParameterTuner.ProbeResult Train, ParameterTuner.ProbeResult Eval)> Probe(int tileSize, int hashQuant,
            int tolerance, int colors)
        {
            var isBaseline = tileSize == 64 && hashQuant == 32 && tolerance == 8 && colors == 0;
            if (isBaseline)
                return Task.FromResult((
                    new ParameterTuner.ProbeResult(BaselinePsnr, 100_000, 500),
                    new ParameterTuner.ProbeResult(BaselinePsnr, 100_000, 500)));

            if (tileSize == 64 && hashQuant == 32 && tolerance == 8 && colors == 16)
                return Task.FromResult((
                    new ParameterTuner.ProbeResult(BaselinePsnr - 0.2, 40_000, 500), // train: cheap, within slack
                    new ParameterTuner.ProbeResult(BaselinePsnr - 10, 40_000, 500))); // eval: collapses

            return Task.FromResult((
                new ParameterTuner.ProbeResult(BaselinePsnr - 5, 200_000, 500),
                new ParameterTuner.ProbeResult(BaselinePsnr - 5, 200_000, 500)));
        }
    }

    [Fact]
    public void BuildSampleWindows_FirstSegment_CentersTheWholeBlockWithinTheSegment()
    {
        // 8000ms segment, block capped at RequiredSampleMs=3000 -- centered means the leftover
        // (8000-3000)=5000ms slack splits evenly, so the block itself starts at 2500ms in.
        var (train, _) = ParameterTuner.BuildSampleWindows(0, 8000, isFirstSegment: true);

        Assert.Equal(2500, train[0].Start); // block start (2500) == first chunk's own start
    }

    [Fact]
    public void BuildSampleWindows_LaterSegment_AnchorsTheWholeBlockAtSegmentStart()
    {
        var (train, _) = ParameterTuner.BuildSampleWindows(2000, 8000, isFirstSegment: false);

        Assert.Equal(2000, train[0].Start); // block (and its first chunk) starts exactly at segmentStartMs
    }

    [Fact]
    public void BuildSampleWindows_AllFourChunks_AreContiguous_InOneLocalBlock_NoOverlap()
    {
        var (train, eval) = ParameterTuner.BuildSampleWindows(0, 8000, isFirstSegment: false);

        // RequiredSampleMs=3000 block anchored at 0 -> 4 contiguous 750ms chunks: train,train,eval,train.
        var chunkMs = ParameterTuner.RequiredSampleMs / 4.0;
        Assert.Equal(3, train.Length);
        Assert.Equal(0, train[0].Start);
        Assert.Equal(chunkMs, train[1].Start);
        Assert.Equal(chunkMs * 2, eval.Start);
        Assert.Equal(chunkMs * 3, train[2].Start);
        Assert.Equal(chunkMs, train[0].DurationMs);
        // all 4 chunks fit inside the RequiredSampleMs block, no overlap between any of them
        Assert.True(train[2].Start + train[2].DurationMs <= ParameterTuner.RequiredSampleMs + 0.001);
    }

    [Fact]
    public void BuildSampleWindows_ScenePreciselyAtRequiredSampleMs_TakesTheShortSceneBranch()
    {
        // Boundary: segmentDurationMs <= RequiredSampleMs is the short-scene case, exactly at the
        // boundary included.
        var (train, eval) = ParameterTuner.BuildSampleWindows(1000, ParameterTuner.RequiredSampleMs, false);

        var t = Assert.Single(train);
        Assert.Equal(1000, t.Start);
        Assert.Equal(ParameterTuner.RequiredSampleMs, t.DurationMs);
        Assert.Equal(t, eval); // eval IS train -- same window, not a separate held-out slice
    }

    [Fact]
    public void BuildSampleWindows_SceneShorterThanRequiredSampleMs_UsesTheWholeSceneAsOneWindow()
    {
        // A scene that IS the whole deliverable -- not a sample of something bigger -- gets tuned
        // against 100% of itself, no held-out eval (overfitting to your own exact deliverable data
        // isn't a risk, it's the goal). No margin-fetch either (removed): this is the scene's own
        // real span, nothing padded in from outside it.
        var (train, eval) = ParameterTuner.BuildSampleWindows(5000, 1200, isFirstSegment: false);

        var t = Assert.Single(train);
        Assert.Equal(5000, t.Start);
        Assert.Equal(1200, t.DurationMs);
        Assert.Equal((5000.0, 1200.0), eval);
    }

    [Fact]
    public void BuildSampleWindows_ScenesJustOverRequiredSampleMs_StillSplitsIntoFourChunks()
    {
        var (train, eval) = ParameterTuner.BuildSampleWindows(0, ParameterTuner.RequiredSampleMs + 1, false);

        Assert.Equal(3, train.Length); // the 4-chunk split branch, not the short-scene single window
    }
}
