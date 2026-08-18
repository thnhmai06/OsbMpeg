using OsbMpeg.Ir;

namespace OsbMpeg.Osbv;

/// <summary>.osbv source-level object. Shares the exact command grammar (SbCommand and its
/// subtypes) with the .osb target IR — commands mean the same thing regardless of which
/// object they're attached to. AnimationVideo is the one object kind with no .osb
/// equivalent: it's a group whose commands the compiler bakes into every generated tile
/// sprite (see the compiler's transform evaluator/baker), not something a renderer plays
/// back directly.</summary>
public abstract class OsbvObject
{
    public required SbLayer Layer { get; init; }
    public required SbOrigin Origin { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public List<SbCommand> Commands { get; init; } = [];
}

public sealed class OsbvSprite : OsbvObject
{
    public required string FilePath { get; init; }
}

public sealed class OsbvAnimation : OsbvObject
{
    public required string FilePath { get; init; }
    public required int FrameCount { get; init; }
    public required double FrameDelayMs { get; init; }
    public required SbLoopType LoopType { get; init; }
}

/// <summary>Fps/VideoStartMs/VideoEndMs default to source video fps / full duration when
/// omitted — the compiler resolves that against the probed source, not the parser.</summary>
public sealed class OsbvAnimationVideo : OsbvObject
{
    public required string FilePath { get; init; }
    public required double StartTimeMs { get; init; }
    public double? Fps { get; init; }
    public double? VideoStartMs { get; init; }
    public double? VideoEndMs { get; init; }
}

public sealed class OsbvDocument
{
    /// <summary>Declaration order == z-order within a layer (matches .osb: no depth sort,
    /// later declarations draw on top). Objects across all layers keep one flat list so an
    /// AnimationVideo expanded into many tile sprites can be spliced back in at the same
    /// declaration position relative to native Sprite/Animation neighbors in the same layer.</summary>
    public List<OsbvObject> Objects { get; } = [];
}
