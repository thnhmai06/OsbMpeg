using OsbMpeg.Compiler.Tuning;
using Xunit;

namespace OsbMpeg.Compiler.Tests;

public class ParameterTunerTests
{
    private const double BaselinePsnr = 30.0;

    [Fact]
    public async Task StaysAtBaseline_WhenNoCandidateIsCheaper()
    {
        var probeCount = 0;

        Task<ParameterTuner.ProbeResult> Probe(int tileSize, int hashQuant, int tolerance, int colors)
        {
            probeCount++;
            var isBaseline = tileSize == 64 && hashQuant == 32 && tolerance == 8 && colors == 0;
            // every non-baseline candidate is both worse AND more expensive -- nothing should win
            return Task.FromResult(isBaseline
                ? new ParameterTuner.ProbeResult(BaselinePsnr, 100_000, 500)
                : new ParameterTuner.ProbeResult(BaselinePsnr - 5, 200_000, 500));
        }

        var tuned = await ParameterTuner.TuneCoreAsync(Probe, "test", null);

        Assert.Equal(new TunedParameters(64, 32, 8, 0), tuned);
        // 3+3+3+4=13 candidates minus 3 seeded (each axis's "unchanged" candidate reuses the
        // previous axis's own winning result instead of re-probing) minus baseline folded into
        // the TileSize axis's own batch (its 64 candidate IS the baseline tuple) = 10.
        Assert.Equal(10, probeCount);
    }

    [Fact]
    public async Task PicksCheaperCandidate_WhenItStaysWithinSlackOfFloor()
    {
        Task<ParameterTuner.ProbeResult> Probe(int tileSize, int hashQuant, int tolerance, int colors)
        {
            // Colors=16 costs half as much and stays within the 1dB slack -- should win its axis.
            if (tileSize == 64 && hashQuant == 32 && tolerance == 8 && colors == 16)
                return Task.FromResult(new ParameterTuner.ProbeResult(BaselinePsnr - 0.5, 50_000, 500));

            var isBaseline = tileSize == 64 && hashQuant == 32 && tolerance == 8 && colors == 0;
            return Task.FromResult(isBaseline
                ? new ParameterTuner.ProbeResult(BaselinePsnr, 100_000, 500)
                : new ParameterTuner.ProbeResult(BaselinePsnr - 5, 200_000, 500));
        }

        var tuned = await ParameterTuner.TuneCoreAsync(Probe, "test", null);

        Assert.Equal(16, tuned.Colors);
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

        Task<ParameterTuner.ProbeResult> Probe(int tileSize, int hashQuant, int tolerance, int colors)
        {
            probeCount++;
            var isBaseline = tileSize == 64 && hashQuant == 32 && tolerance == 8 && colors == 0;
            return Task.FromResult(isBaseline
                ? new ParameterTuner.ProbeResult(BaselinePsnr, 100_000, 500)
                : new ParameterTuner.ProbeResult(BaselinePsnr - 50, 200_000, 500));
        }

        var tuned = await ParameterTuner.TuneCoreAsync(Probe, "test", null);

        Assert.Equal(new TunedParameters(64, 32, 8, 0), tuned);
        Assert.Equal(10, probeCount); // no extra re-probe of the seeded (64,32,8,0) tuple anywhere
    }
}
