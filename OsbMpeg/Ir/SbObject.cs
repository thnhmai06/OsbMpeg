namespace OsbMpeg.Ir;

public abstract class SbObject
{
    public required SbLayer Layer { get; init; }
    public required SbOrigin Origin { get; init; }
    public required float X { get; init; }
    public required float Y { get; init; }
    public required List<SbCommand> Commands { get; init; }

    /// <summary>Free-text note on why the encoder chose this representation. Diagnostics only.</summary>
    public string? Provenance { get; init; }

    /// <summary>Per osu! semantics: only objects with >=1 non-trigger command are ever
    /// instantiated by the renderer (HasCommands excludes TriggerGroups).</summary>
    public bool HasCommands => Commands.Any(c => c is not SbTrigger);
}

public sealed class SbSprite : SbObject
{
    public required AssetId Asset { get; init; }
}

public enum SbLoopType
{
    LoopForever = 0,
    LoopOnce = 1,
}

public sealed class SbAnimation : SbObject
{
    public required AssetId[] Frames { get; init; }
    public required double FrameDelayMs { get; init; }
    public SbLoopType LoopType { get; init; } = SbLoopType.LoopForever;
}
