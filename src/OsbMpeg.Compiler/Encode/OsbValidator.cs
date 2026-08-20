using OsbMpeg.Parsers.Ir;
using OsbMpeg.Parsers.Osb;

namespace OsbMpeg.Compiler.Encode;

/// <summary>
///     Round-trip sanity check: decode what we just wrote with OsuParsers' own
///     parser and confirm the object count matches.
/// </summary>
public static class OsbValidator
{
    public static void Validate(string osbPath, SbDocument doc)
    {
        var decoded = StoryboardDecoderGate.Decode(osbPath);

        var decodedCount = decoded.BackgroundLayer.Count + decoded.FailLayer.Count
                                                         + decoded.PassLayer.Count + decoded.ForegroundLayer.Count +
                                                         decoded.OverlayLayer.Count;
        var expectedCount = doc.AllObjects.Count(o => o.HasCommands);

        if (decodedCount != expectedCount)
            throw new InvalidOperationException(
                $"OsbWriter produced a document that OsuParsers decodes with a different object count: wrote {expectedCount}, decoded {decodedCount}. The .osb at {osbPath} is likely malformed.");
    }
}