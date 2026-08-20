using OsbMpeg.Compiler.Detection;
using Xunit;

namespace OsbMpeg.Compiler.Tests;

public class ScenePrePassTests
{
    private const int TileCount = 100;

    private static List<double> Detect(IEnumerable<(double Ms, int ClosedCount)> frames)
    {
        return ScenePrePass.DetectCuts(frames, TileCount);
    }

    [Fact]
    public void NoFramesExceedThreshold_NoCutsFound()
    {
        var frames = new (double, int)[]
        {
            (0, 5), (33, 10), (67, 8), (100, 12) // ordinary low churn, well under CutThreshold
        };

        Assert.Empty(Detect(frames));
    }

    [Fact]
    public void FrameAtOrAboveThreshold_IsACut()
    {
        var frames = new (double, int)[]
        {
            (0, 5), (33, 10), (5000, 95), (5033, 8) // frame at 5000ms: 95/100 = 0.95 >= 0.7
        };

        var cuts = Detect(frames);

        Assert.Equal([5000.0], cuts);
    }

    [Fact]
    public void ConsecutiveHighFramesWithinBurstGap_CollapseToOneCandidateAtBurstStart()
    {
        // A real cut can spike the closed-fraction on a couple of adjacent frames (e.g. a short
        // fade), not just one -- these are one physical event, collapsed to its own start.
        var frames = new (double, int)[] { (5000, 90), (5033, 92), (5067, 5) };

        Assert.Equal([5000.0], Detect(frames));
    }

    [Fact]
    public void ABriefDipBelowThreshold_DoesNotSplitOneBurst()
    {
        // Sustained high-motion content can dip below CutThreshold for a frame or two without
        // being a separate event -- as long as the gap since the last high frame stays within
        // BurstGapMs, it's still the same burst.
        var frames = new (double, int)[] { (0, 90), (33, 40), (67, 92) }; // 40/100 = 0.4, a dip

        Assert.Equal([0.0], Detect(frames));
    }

    [Fact]
    public void TwoGenuinelySeparateCuts_FarApart_BothSurvive()
    {
        var frames = new (double, int)[] { (0, 90), (5000, 92) }; // 5000ms apart, both real

        Assert.Equal([0.0, 5000.0], Detect(frames));
    }

    [Fact]
    public void DistinctEventsWellOverMinSceneMsApart_AllSurvive_EvenIfCloserThanTheOldFixedWindow()
    {
        // Real fixture data (see docs/research.md): a genuine hard cut (bad_apple->birdbrain), a real
        // sustained turbulent stretch inside birdbrain's own footage ~1.6s later, and the next
        // real hard cut (birdbrain->fish_spinning) ~1.8s after that. A first design suppressed
        // anything within a fixed 2s of the previously accepted candidate on the theory that close
        // events are noise -- that swallowed the middle cut here. Each gap below is well over
        // MinSceneMs (1000), so per this feature's own definition of a scene (the longest span one
        // tuned combo stays a good fit), all three are real, distinct scenes and must all survive.
        var frames = new (double, int)[]
        {
            (10000, 90), // bad_apple -> birdbrain
            (11633, 90), // turbulence inside birdbrain, 1633ms later
            (13167, 90) // birdbrain -> fish_spinning-ish, 1534ms later
        };

        Assert.Equal([10000.0, 11633.0, 13167.0], Detect(frames));
    }

    [Fact]
    public void CandidatesWithinMinSceneMs_OnlyTheFirstSurvives_GuardsDegenerateShortScenes()
    {
        var frames = new (double, int)[] { (0, 90), (400, 92) }; // 400ms apart, well under MinSceneMs (1000)

        Assert.Equal([0.0], Detect(frames));
    }

    [Fact]
    public void ZeroTileCount_NeverThrows_NoCutsFound()
    {
        var cuts = ScenePrePass.DetectCuts([(0, 0), (33, 0)], 0);
        Assert.Empty(cuts);
    }

    [Fact]
    public void PlanMargins_WindowAlreadyLongEnough_NoMarginRequested()
    {
        // Window is 10000ms, requiredSampleMs is 1500 -- plenty already, no internal cuts.
        var plan = ScenePrePass.PlanMargins(0, 10000, [], 1500, 100000);

        Assert.Null(plan.LeadInStartMs);
        Assert.Null(plan.TrailOutEndMs);
    }

    [Fact]
    public void PlanMargins_ShortWindowNoInternalCuts_RequestsBothMargins()
    {
        // Window is only 500ms, well under requiredSampleMs -- both edges are "outer" edges here
        // (no internal cut splits it), so both get a margin request (see PlanMargins' own doc
        // comment on why this over-fetches slightly rather than sequencing the two checks).
        var plan = ScenePrePass.PlanMargins(10000, 10500, [], 1500, 100000);

        Assert.Equal(9000, plan.LeadInStartMs); // 10000 - (1500 - 500)
        Assert.Equal(11500, plan.TrailOutEndMs); // 10500 + (1500 - 500)
    }

    [Fact]
    public void PlanMargins_LeadInCappedAtZero_NeverRequestsBeforeFileStart()
    {
        var plan = ScenePrePass.PlanMargins(500, 1000, [], 1500, 100000);

        Assert.Equal(0, plan.LeadInStartMs); // would be 500-1000=-500, clamped to 0
    }

    [Fact]
    public void PlanMargins_TrailOutCappedAtFileDuration_NeverRequestsPastFileEnd()
    {
        var plan = ScenePrePass.PlanMargins(98500, 99000, [], 1500, 99500);

        Assert.Equal(99500, plan.TrailOutEndMs); // would be 99000+1000=100000, clamped to 99500
    }

    [Fact]
    public void PlanMargins_InternalCutSplitsWindow_OnlyOuterEdgesConsidered()
    {
        // Window [0,10000) with one internal cut at 9800 -> sub-windows [0,9800) (plenty long) and
        // [9800,10000) (only 200ms, short) -- only the trailing edge needs a margin, and the
        // leading edge is measured against the FIRST sub-window (0..9800, already long enough),
        // not against the whole window.
        var plan = ScenePrePass.PlanMargins(0, 10000, [9800], 1500, 100000);

        Assert.Null(plan.LeadInStartMs);
        Assert.Equal(11300, plan.TrailOutEndMs); // 10000 + (1500 - 200)
    }
}
