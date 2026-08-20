using OsbMpeg.Compiler.Compilation;
using OsbMpeg.Compiler.Shared.Media;
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
    }

    [Fact]
    public async Task RequestedFpsAboveSource_UpsamplesByDuplication_EqualRequestsShare()
    {
        var info = new Dictionary<string, MediaInfo>
            { [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264") };
        var videos = new[] { Video("a.mp4", 0, 60), Video("a.mp4", 5000, 60) }; // 60 requested > source 30: ffmpeg duplicates to reach it

        var plans = await VideoSourcePlanner.PlanAsync(videos, FakeProbe(info));

        var item = Assert.Single(plans);
        Assert.Equal(60, item.Key.EffectiveFps); // requested rate wins; no clamp to source
        Assert.Equal(2, item.Members.Count); // same file at the same requested rate still shares one decode
    }

    [Fact]
    public async Task DifferentRequestedFps_SameFile_GetSeparatePlans()
    {
        var info = new Dictionary<string, MediaInfo>
            { [Path.GetFullPath("a.mp4")] = new(1920, 1080, 30, TimeSpan.FromSeconds(10), "h264") };
        var videos = new[] { Video("a.mp4", 0, 60), Video("a.mp4", 5000) }; // null fps keeps the source rate

        var plans = await VideoSourcePlanner.PlanAsync(videos, FakeProbe(info));

        Assert.Equal(2, plans.Count); // 60fps and 30fps samples of one file are different decode jobs
        Assert.Equal(60, plans[0].Key.EffectiveFps);
        Assert.Equal(30, plans[1].Key.EffectiveFps);
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
        Assert.Contains("b.mp4", plans[0].Key.NormalizedPath);
        Assert.Contains("a.mp4", plans[1].Key.NormalizedPath);
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