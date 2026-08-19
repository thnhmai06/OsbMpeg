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
        Assert.Equal(14, probeCount); // baseline + 3 + 3 + 3 + 4 axis candidates, no redundant final re-probe
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
    public async Task FallsBackToBaseline_WhenLastAxisNeverMeetsFloor()
    {
        var callCount = 0;

        Task<ParameterTuner.ProbeResult> Probe(int tileSize, int hashQuant, int tolerance, int colors)
        {
            callCount++;
            // First 10 calls (baseline + TileSize + Colors + HashQuantLevels sweeps: 1+3+3+3) pass
            // comfortably; the last axis (TileTolerance, the remaining 4 calls) never meets the
            // floor -- the one condition that forces TuneCoreAsync's baseline fallback.
            var psnr = callCount <= 10 ? BaselinePsnr : BaselinePsnr - 50;
            return Task.FromResult(new ParameterTuner.ProbeResult(psnr, 100, 1));
        }

        var tuned = await ParameterTuner.TuneCoreAsync(Probe, "test", null);

        Assert.Equal(new TunedParameters(64, 32, 8, 0), tuned);
    }
}
