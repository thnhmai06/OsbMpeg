namespace OsbMpeg.Compiler.Encode;

public sealed class EncodeStatistics
{
    public required string InputPath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double Fps { get; init; }
    public required TimeSpan Duration { get; init; }
    public required int FrameCount { get; init; }

    public required int SpriteCount { get; init; }
    public required int AnimationCount { get; init; }
    public required int CommandCount { get; init; }
    public required int AssetCount { get; init; }
    public required int AnimationFrameCount { get; init; }
    public required long AssetBytes { get; init; }

    public required long RawFrameBytes { get; init; }
    public required long OsbFileBytes { get; init; }
    public required long SourceFileBytes { get; init; }

    /// <summary>
    ///     Estimated size of the "dump every frame as its own sprite" strawman — see
    ///     NaiveBaseline. The denominator that actually proves the tile codec does something.
    /// </summary>
    public required long NaiveEstimatedBytes { get; init; }

    public required TimeSpan EncodeTime { get; init; }

    private long StoryboardBytes => OsbFileBytes + AssetBytes;

    /// <summary>
    ///     Reduction vs. the uncompressed raw RGB frame stream — a flattering upper
    ///     bound, not proof the codec does anything (a frame-by-frame storyboard would beat this
    ///     too). Kept as one of the two MVP denominators; the "vs naive frame-by-frame storyboard"
    ///     denominator that actually proves something is phase 10 (post-MVP).
    /// </summary>
    public double ReductionVsRawFrames => RawFrameBytes == 0 ? 0 : 1.0 - (double)StoryboardBytes / RawFrameBytes;

    public double ReductionVsSourceFile => SourceFileBytes == 0 ? 0 : 1.0 - (double)StoryboardBytes / SourceFileBytes;

    public double ReductionVsNaive =>
        NaiveEstimatedBytes == 0 ? 0 : 1.0 - (double)StoryboardBytes / NaiveEstimatedBytes;
}