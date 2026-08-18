namespace OsbMpeg.Encoder;

public sealed record EncodeProgress(
    int FrameIndex,
    int EstimatedTotalFrames,
    double CurrentTimeSeconds,
    int SpriteCount,
    int CommandCount,
    int AssetCount,
    long AssetBytes);
