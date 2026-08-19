namespace OsbMpeg.Parsers.Ir;

/// <summary>
///     Path of an asset relative to the .osb file, e.g. "sb/a3f9c1.png".
///     Two objects sharing an AssetId share the same file (content-hash deduped by AssetStore).
/// </summary>
public readonly record struct AssetId(string RelativePath)
{
    public override string ToString()
    {
        return RelativePath;
    }
}