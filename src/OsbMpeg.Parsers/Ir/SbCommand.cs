namespace OsbMpeg.Parsers.Ir;

/// <summary>
///     Scalar/flag command kind. Move is always split into MoveX/MoveY (matches how
///     the legacy decoder expands M internally); Colour carries an RGB triple, not a scalar.
/// </summary>
public enum SbCommandKind : byte
{
    Fade,
    MoveX,
    MoveY,
    Scale,
    VectorScaleX,
    VectorScaleY,
    Rotate,
    Colour,
    FlipH,
    FlipV,
    Additive
}

public abstract class SbCommand
{
    public required double StartMs { get; init; }
    public required double EndMs { get; init; }
    public SbEasing Easing { get; init; } = SbEasing.None;
}

/// <summary>
///     Fade/MoveX/MoveY/Scale/VectorScaleX/VectorScaleY/Rotate. Rotate is in radians
///     (matches the .osb wire format; the renderer converts to degrees internally).
/// </summary>
public sealed class SbValueCommand : SbCommand
{
    public required SbCommandKind Kind { get; init; }
    public required float Start { get; init; }
    public required float End { get; init; }
}

public sealed class SbColourCommand : SbCommand
{
    public required SbColor Start { get; init; }
    public required SbColor End { get; init; }
}

/// <summary>
///     P command (FlipH/FlipV/Additive). Per osu! semantics this has no distinct
///     start/end value: StartMs==EndMs makes it permanent from that point on; otherwise it
///     is active only during [StartMs,EndMs) and reverts afterward.
/// </summary>
public sealed class SbFlagCommand : SbCommand
{
    public required SbCommandKind Kind { get; init; }
}

/// <summary>
///     L command. EndMs is unused (loops have no explicit end time in the format);
///     child command times are relative to StartMs. Runs max(0,Count-1)+1 times, minimum 1.
/// </summary>
public sealed class SbLoop : SbCommand
{
    public required int Count { get; init; }
    public required List<SbCommand> Children { get; init; }
}

/// <summary>
///     T command. Child command times are relative to the trigger firing time, not
///     to StartMs. Not used by the MVP encoder (osu! never fires triggers we generate without
///     gameplay context) but supported for round-trip completeness.
/// </summary>
public sealed class SbTrigger : SbCommand
{
    public required string Name { get; init; }
    public int Group { get; init; }
    public required List<SbCommand> Children { get; init; }
}