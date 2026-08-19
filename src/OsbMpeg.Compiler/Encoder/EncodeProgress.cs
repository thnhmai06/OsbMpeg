namespace OsbMpeg.Compiler.Encoder;

public sealed record EncodeProgress(
    int FrameIndex,
    int EstimatedTotalFrames,
    int SpriteCount,
    int CommandCount,
    int AssetCount,
    long AssetBytes);