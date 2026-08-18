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

/// <summary>osu! derives EVERY frame's file — including frame 0 — from BasePath via
/// `BasePath.Replace(".", "{i}.")` (verified against LegacyStoryboardDecoder /
/// DrawableStoryboardAnimation; it replaces every dot in the path, not just the extension
/// separator). BasePath itself is never read as an image. Storing a resolved per-frame array
/// instead of BasePath+FrameCount would be a trap: writing an already-substituted "frame 0"
/// path as the .osb's literal path field would get substituted a second time by the real
/// client, producing the wrong file.</summary>
public sealed class SbAnimation : SbObject
{
    public required AssetId BasePath { get; init; }
    public required int FrameCount { get; init; }
    public required double FrameDelayMs { get; init; }
    public SbLoopType LoopType { get; init; } = SbLoopType.LoopForever;

    public AssetId FramePath(int frameIndex) => new(BasePath.RelativePath.Replace(".", $"{frameIndex}."));
}
