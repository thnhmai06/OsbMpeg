namespace OsbMpeg.Compiler.Encode;

public sealed record EncodeProgress(
    int FrameIndex,
    int EstimatedTotalFrames,
    int SpriteCount,
    int CommandCount,
    int AssetCount,
    long AssetBytes);