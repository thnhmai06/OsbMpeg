using OsbMpeg.Compiler.Compilation;
using OsbMpeg.Compiler.Media;
using OsbMpeg.Parsers.Ir;
using OsbMpeg.Parsers.Osbv;
using Xunit;

namespace OsbMpeg.Compiler.Tests;

public class VideoSourcePlannerTests
{
    private static OsbvAnimationVideo Video(string path, double startTime, double? fps = null,
        double? videoStart = null, double? videoEnd = null)
    {
        return new OsbvAnimationVideo
        {
            Layer = SbLayer.Foreground,
            Origin = SbOrigin.Centre,
            X = 0,
            Y = 0,
            FilePath = path,
            StartTimeMs = startTime,
            Fps = fps,
            VideoStartMs = videoStart,
            VideoEndMs = videoEnd
        };
    }

    private static Func<string, Task<MediaInfo>> FakeProbe(Dictionary<string, MediaInfo> byPath)
    {
        return path => Task.FromResult(byPath[Path.GetFullPath(path)]);
    }

    [Fact]
    public async Task SameFileSameEffectiveFps_SharesOnePlan()
    {
        var info = new Dictionary<string, MediaInfo>
            { [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264") };
        var videos = new[] { Video("a.mp4", 0), Video("a.mp4", 5000) };

        var plans = await VideoSourcePlanner.PlanAsync(videos, FakeProbe(info));

        Assert.Single(plans);
        Assert.Equal(2, plans[0].Members.Count);
        Assert.False(string.IsNullOrEmpty(plans[0].VideoId));
    }

    [Fact]
    public async Task RequestedFpsAboveSource_ClampsToSource_StillShares()
    {
        var info = new Dictionary<string, MediaInfo>
            { [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264") };
        var videos = new[] { Video("a.mp4", 0, 60), Video("a.mp4", 5000) }; // 60 requested clamps to source's 30

        var plans = await VideoSourcePlanner.PlanAsync(videos, FakeProbe(info));

        var item = Assert.Single(plans);
        Assert.Equal(30, item.Key.EffectiveFps);
    }

    [Fact]
    public async Task DifferentFiles_GetSeparatePlansInFirstSeenOrder()
    {
        var info = new Dictionary<string, MediaInfo>
        {
            [Path.GetFullPath("b.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264"),
            [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264")
        };
        var videos = new[] { Video("b.mp4", 0), Video("a.mp4", 0) };

        var plans = await VideoSourcePlanner.PlanAsync(videos, FakeProbe(info));

        Assert.Equal(2, plans.Count);
        Assert.NotEqual(plans[0].VideoId, plans[1].VideoId);
        Assert.Contains("b.mp4", plans[0].Key.NormalizedPath);
        Assert.Contains("a.mp4", plans[1].Key.NormalizedPath);
    }

    [Fact]
    public async Task VideoId_IsStableRegardlessOfOtherSourcesOrOrder()
    {
        var info = new Dictionary<string, MediaInfo>
        {
            [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264"),
            [Path.GetFullPath("b.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264")
        };

        // "a.mp4" is first in one document, second in another (e.g. a different .osbv project) --
        // its VideoId must be identical both times, since it's the same underlying video/fps and
        // that's the key the persistent asset cache and scene cache both share on.
        var plansA = await VideoSourcePlanner.PlanAsync([Video("a.mp4", 0), Video("b.mp4", 0)], FakeProbe(info));
        var plansB = await VideoSourcePlanner.PlanAsync([Video("b.mp4", 0), Video("a.mp4", 0)], FakeProbe(info));

        var aVideoId = plansA.Single(p => p.Key.NormalizedPath.Contains("a.mp4")).VideoId;
        var aVideoIdAgain = plansB.Single(p => p.Key.NormalizedPath.Contains("a.mp4")).VideoId;
        Assert.Equal(aVideoId, aVideoIdAgain);
    }

    [Fact]
    public async Task UnionRange_CoversAllMembers_DefaultingToFullDuration()
    {
        var info = new Dictionary<string, MediaInfo>
            { [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264") };
        var videos = new[]
        {
            Video("a.mp4", 0, videoStart: 2000, videoEnd: 4000),
            Video("a.mp4", 5000) // no explicit range -> defaults to [0, full duration]
        };

        var plans = await VideoSourcePlanner.PlanAsync(videos, FakeProbe(info));

        Assert.Equal(0, plans[0].UnionStartMs);
        Assert.Equal(10000, plans[0].UnionEndMs);
    }
}