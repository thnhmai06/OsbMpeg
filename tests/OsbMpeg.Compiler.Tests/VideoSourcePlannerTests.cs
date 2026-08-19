using OsbMpeg.Ir;
using OsbMpeg.Media;
using OsbMpeg.Osbv;
using OsbMpeg.VideoCompilation;
using Xunit;

namespace OsbMpeg.Tests;

public class VideoSourcePlannerTests
{
    private static OsbvAnimationVideo Video(string path, double startTime, double? fps = null, double? videoStart = null, double? videoEnd = null) => new()
    {
        Layer = SbLayer.Foreground,
        Origin = SbOrigin.Centre,
        X = 0,
        Y = 0,
        FilePath = path,
        StartTimeMs = startTime,
        Fps = fps,
        VideoStartMs = videoStart,
        VideoEndMs = videoEnd,
    };

    private static Func<string, Task<MediaInfo>> FakeProbe(Dictionary<string, MediaInfo> byPath) =>
        path => Task.FromResult(byPath[Path.GetFullPath(path)]);

    [Fact]
    public async Task SameFileSameEffectiveFps_SharesOnePlan()
    {
        var info = new Dictionary<string, MediaInfo> { [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264") };
        var videos = new[] { Video("a.mp4", 0), Video("a.mp4", 5000) };

        var plans = await VideoSourcePlanner.PlanAsync(videos, FakeProbe(info));

        Assert.Single(plans);
        Assert.Equal(2, plans[0].Members.Count);
        Assert.Equal("0", plans[0].VideoId);
    }

    [Fact]
    public async Task RequestedFpsAboveSource_ClampsToSource_StillShares()
    {
        var info = new Dictionary<string, MediaInfo> { [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264") };
        var videos = new[] { Video("a.mp4", 0, fps: 60), Video("a.mp4", 5000) }; // 60 requested clamps to source's 30

        var plans = await VideoSourcePlanner.PlanAsync(videos, FakeProbe(info));

        Assert.Single(plans);
        Assert.Equal(30, plans[0].Key.EffectiveFps);
    }

    [Fact]
    public async Task DifferentFiles_GetSeparatePlansInFirstSeenOrder()
    {
        var info = new Dictionary<string, MediaInfo>
        {
            [Path.GetFullPath("b.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264"),
            [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264"),
        };
        var videos = new[] { Video("b.mp4", 0), Video("a.mp4", 0) };

        var plans = await VideoSourcePlanner.PlanAsync(videos, FakeProbe(info));

        Assert.Equal(2, plans.Count);
        Assert.Equal("0", plans[0].VideoId);
        Assert.Equal("1", plans[1].VideoId);
        Assert.Contains("b.mp4", plans[0].Key.NormalizedPath);
        Assert.Contains("a.mp4", plans[1].Key.NormalizedPath);
    }

    [Fact]
    public async Task UnionRange_CoversAllMembers_DefaultingToFullDuration()
    {
        var info = new Dictionary<string, MediaInfo> { [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264") };
        var videos = new[]
        {
            Video("a.mp4", 0, videoStart: 2000, videoEnd: 4000),
            Video("a.mp4", 5000), // no explicit range -> defaults to [0, full duration]
        };

        var plans = await VideoSourcePlanner.PlanAsync(videos, FakeProbe(info));

        Assert.Equal(0, plans[0].UnionStartMs);
        Assert.Equal(10000, plans[0].UnionEndMs);
    }
}
