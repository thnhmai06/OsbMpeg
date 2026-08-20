namespace OsbMpeg.Compiler.Compilation;

/// <summary>
///     Identifies a video decode job that can be shared across multiple AnimationVideo
///     declarations: the same file sampled at the same frame rate produces the same frames, so
///     two AnimationVideo entries pointing at it only need one ffmpeg decode between them no
///     matter how their [videoStart,videoEnd] windows or on-canvas transforms differ.
///     Time range is deliberately NOT part of the key — folding it in would defeat the sharing
///     this key exists to enable. Range only affects the union-extract window
///     (VideoSourcePlanner) and each member's own run/sprite emission later; it never affects
///     whether two AnimationVideo can share one decode.
/// </summary>
public readonly record struct VideoSourceKey(string NormalizedPath, double EffectiveFps)
{
    /// <summary>
    ///     The storyboard runs at the requested frame rate; ffmpeg's fps filter resamples to
    ///     reach it. When requested &gt; source the filter duplicates frames (no new information,
    ///     but a user who asks for e.g. a 60fps timeline against a 24fps source gets exactly
    ///     that cadence); when requested &lt; source it drops frames as before. Null means the
    ///     source's own rate.
    /// </summary>
    public static VideoSourceKey Create(string filePath, double? requestedFps, double sourceFps)
    {
        var effective = requestedFps ?? sourceFps;
        // Windows paths are case-insensitive; fold case so "Video.MP4" and "video.mp4" hit
        // the same key instead of decoding the same file twice.
        var normalized = Path.GetFullPath(filePath).ToLowerInvariant();
        return new VideoSourceKey(normalized, effective);
    }
}