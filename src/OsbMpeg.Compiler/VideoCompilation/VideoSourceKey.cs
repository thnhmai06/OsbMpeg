namespace OsbMpeg.VideoCompilation;

/// <summary>Identifies a video decode job that can be shared across multiple AnimationVideo
/// declarations: the same file sampled at the same frame rate produces the same frames, so
/// two AnimationVideo entries pointing at it only need one ffmpeg decode between them no
/// matter how their [videoStart,videoEnd] windows or on-canvas transforms differ.
///
/// Time range is deliberately NOT part of the key — folding it in would defeat the sharing
/// this key exists to enable. Range only affects the union-extract window
/// (VideoSourcePlanner) and each member's own run/sprite emission later; it never affects
/// whether two AnimationVideo can share one decode.</summary>
public readonly record struct VideoSourceKey(string NormalizedPath, double EffectiveFps)
{
    /// <summary>effectiveFps = min(requested, source) — never duplicates frames to reach a
    /// requested rate above the source's own, since that produces no new information and
    /// just inflates frame/asset count for free.</summary>
    public static VideoSourceKey Create(string filePath, double? requestedFps, double sourceFps)
    {
        var effective = Math.Min(requestedFps ?? sourceFps, sourceFps);
        // Windows paths are case-insensitive; fold case so "Video.MP4" and "video.mp4" hit
        // the same key instead of decoding the same file twice.
        var normalized = Path.GetFullPath(filePath).ToLowerInvariant();
        return new VideoSourceKey(normalized, effective);
    }
}
