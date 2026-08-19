using OsuParsers.Decoders;
using OsuParsers.Storyboards;

namespace OsbMpeg.Parsers.Osb;

/// <summary>
///     StoryboardDecoder is a static class with private static state (not reentrant —
///     see OsuParsers issue #43). Every call in this codebase goes through here so it's
///     serialized behind one lock instead of each caller inventing its own.
/// </summary>
public static class StoryboardDecoderGate
{
    private static readonly Lock Gate = new();

    public static Storyboard Decode(string path)
    {
        lock (Gate)
        {
            return StoryboardDecoder.Decode(path);
        }
    }
}