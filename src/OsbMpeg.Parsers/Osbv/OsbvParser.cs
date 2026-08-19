using System.Globalization;
using OsbMpeg.Ir;

namespace OsbMpeg.Osbv;

/// <summary>Hand-written recursive-descent-over-indentation parser for the .osbv source
/// format: an .osb superset that adds AnimationVideo as a group-transform object. Command
/// grammar (F/M/MX/MY/S/V/R/C/P/L/T) is byte-for-byte the same as .osb — see
/// osu.ppy.sh/wiki/en/Storyboard/Scripting/Commands. Trigger ("T") parses fully (its
/// gameplay-firing semantics are unaffected by grammar), but GroupTransformBaker restricts
/// what an AnimationVideo's Trigger children may contain — see that class.
///
/// Indentation is a general stack, not a fixed two-level scheme: depth is the count of
/// leading space/underscore characters (identical weight, matching .osb), and a line at
/// depth d attaches to the nearest still-open frame at depth d-1. That's what lets L nest
/// inside L arbitrarily deep, same as inside a real .osb.</summary>
public static class OsbvParser
{
    public static OsbvDocument ParseFile(string path) => Parse(File.ReadLines(path));

    public static OsbvDocument Parse(IEnumerable<string> lines)
    {
        var doc = new OsbvDocument();
        var lineNo = 0;
        var sawHeader = false;

        OsbvObject? currentObject = null;
        var stack = new List<(int Depth, List<SbCommand> Target)>();

        foreach (var raw in lines)
        {
            lineNo++;
            if (raw.Length == 0)
                continue;

            var trimmedStart = raw.TrimStart(' ', '_');
            if (trimmedStart.Length == 0 || trimmedStart.StartsWith("//"))
                continue;

            if (!sawHeader)
            {
                if (trimmedStart.TrimEnd() != "OsbV: 1")
                    throw new OsbvParseException(lineNo, $"expected version header \"OsbV: 1\", got \"{trimmedStart.TrimEnd()}\"");
                sawHeader = true;
                continue;
            }

            var depth = raw.Length - trimmedStart.Length;
            var fields = SplitFields(trimmedStart);

            if (depth == 0)
            {
                currentObject = ParseObjectHeader(fields, lineNo);
                doc.Objects.Add(currentObject);
                stack.Clear();
                stack.Add((0, currentObject.Commands));
                continue;
            }

            if (currentObject is null)
                throw new OsbvParseException(lineNo, "command line before any object header");

            while (stack.Count > 0 && stack[^1].Depth >= depth)
                stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0 || stack[^1].Depth != depth - 1)
                throw new OsbvParseException(lineNo, $"unexpected indentation (depth {depth} with no open depth-{depth - 1} block)");

            var target = stack[^1].Target;
            var cmd = ParseCommand(fields, lineNo);
            target.AddRange(cmd);

            if (cmd is [SbLoop loop])
                stack.Add((depth, loop.Children));
            else if (cmd is [SbTrigger trigger])
                stack.Add((depth, trigger.Children));
        }

        if (!sawHeader)
            throw new OsbvParseException(lineNo, "empty file — missing version header \"OsbV: 1\"");

        return doc;
    }

    private static OsbvObject ParseObjectHeader(List<string> f, int lineNo)
    {
        switch (f[0])
        {
            case "Sprite":
                RequireCount(f, 6, lineNo, "Sprite,layer,origin,\"path\",x,y");
                return new OsbvSprite
                {
                    Layer = ParseLayer(f[1], lineNo),
                    Origin = ParseOrigin(f[2], lineNo),
                    FilePath = f[3],
                    X = ParseDouble(f[4], lineNo),
                    Y = ParseDouble(f[5], lineNo),
                };

            case "Animation":
                RequireCount(f, 9, lineNo, "Animation,layer,origin,\"path\",x,y,frameCount,frameDelay,loopType");
                return new OsbvAnimation
                {
                    Layer = ParseLayer(f[1], lineNo),
                    Origin = ParseOrigin(f[2], lineNo),
                    FilePath = f[3],
                    X = ParseDouble(f[4], lineNo),
                    Y = ParseDouble(f[5], lineNo),
                    FrameCount = ParseInt(f[6], lineNo),
                    FrameDelayMs = ParseDouble(f[7], lineNo),
                    LoopType = Enum.Parse<SbLoopType>(f[8]),
                };

            case "AnimationVideo":
                if (f.Count < 7 || f.Count > 10)
                    throw new OsbvParseException(lineNo, $"AnimationVideo,layer,origin,\"path\",x,y,startTime[,fps[,videoStartTime[,videoEndTime]]] — got {f.Count} fields");
                return new OsbvAnimationVideo
                {
                    Layer = ParseLayer(f[1], lineNo),
                    Origin = ParseOrigin(f[2], lineNo),
                    FilePath = f[3],
                    X = ParseDouble(f[4], lineNo),
                    Y = ParseDouble(f[5], lineNo),
                    StartTimeMs = ParseDouble(f[6], lineNo),
                    Fps = f.Count > 7 && f[7].Length > 0 ? ParseDouble(f[7], lineNo) : null,
                    VideoStartMs = f.Count > 8 && f[8].Length > 0 ? ParseDouble(f[8], lineNo) : null,
                    VideoEndMs = f.Count > 9 && f[9].Length > 0 ? ParseDouble(f[9], lineNo) : null,
                };

            case "T":
                throw new OsbvParseException(lineNo, "Trigger objects are not an object header — malformed file");

            default:
                throw new OsbvParseException(lineNo, $"unknown object type \"{f[0]}\"");
        }
    }

    /// <summary>Returns 1 command normally, 2 for M/V (expanded to X+Y components, matching
    /// how OsbReader expands OsuParsers' Movement/VectorScale). Wrapped in a list so the
    /// caller can special-case "did this line open an L block" without a type switch.</summary>
    private static List<SbCommand> ParseCommand(List<string> f, int lineNo)
    {
        if (f[0] == "T")
        {
            RequireEither(f, 4, 5, lineNo, "T,triggerType,startTime,endTime[,groupNumber]");
            var group = f.Count > 4 && f[4].Length > 0 ? ParseInt(f[4], lineNo) : 0;
            return [new SbTrigger { Name = f[1], StartMs = ParseDouble(f[2], lineNo), EndMs = ParseDouble(f[3], lineNo), Group = group, Children = [] }];
        }

        if (f[0] == "L")
        {
            RequireCount(f, 3, lineNo, "L,startTime,loopCount");
            var start = ParseDouble(f[1], lineNo);
            return [new SbLoop { StartMs = start, EndMs = start, Count = ParseInt(f[2], lineNo), Children = [] }];
        }

        var easing = (SbEasing)ParseInt(f[1], lineNo);
        var startMs = ParseDouble(f[2], lineNo);
        var endMs = f[3].Length > 0 ? ParseDouble(f[3], lineNo) : startMs; // shorthand: omitted end time == start time

        switch (f[0])
        {
            case "F": return [ScalarCommand(SbCommandKind.Fade, f, 4, easing, startMs, endMs, lineNo)];
            case "MX": return [ScalarCommand(SbCommandKind.MoveX, f, 4, easing, startMs, endMs, lineNo)];
            case "MY": return [ScalarCommand(SbCommandKind.MoveY, f, 4, easing, startMs, endMs, lineNo)];
            case "S": return [ScalarCommand(SbCommandKind.Scale, f, 4, easing, startMs, endMs, lineNo)];
            case "R": return [ScalarCommand(SbCommandKind.Rotate, f, 4, easing, startMs, endMs, lineNo)];

            case "M":
            {
                RequireEither(f, 6, 8, lineNo, "M,easing,start,end,startX,startY[,endX,endY]");
                var (sx, sy) = (ParseFloat(f[4], lineNo), ParseFloat(f[5], lineNo));
                var (ex, ey) = f.Count > 6 ? (ParseFloat(f[6], lineNo), ParseFloat(f[7], lineNo)) : (sx, sy);
                return
                [
                    new SbValueCommand { Kind = SbCommandKind.MoveX, StartMs = startMs, EndMs = endMs, Easing = easing, Start = sx, End = ex },
                    new SbValueCommand { Kind = SbCommandKind.MoveY, StartMs = startMs, EndMs = endMs, Easing = easing, Start = sy, End = ey },
                ];
            }

            case "V":
            {
                RequireEither(f, 6, 8, lineNo, "V,easing,start,end,startX,startY[,endX,endY]");
                var (sx, sy) = (ParseFloat(f[4], lineNo), ParseFloat(f[5], lineNo));
                var (ex, ey) = f.Count > 6 ? (ParseFloat(f[6], lineNo), ParseFloat(f[7], lineNo)) : (sx, sy);
                return
                [
                    new SbValueCommand { Kind = SbCommandKind.VectorScaleX, StartMs = startMs, EndMs = endMs, Easing = easing, Start = sx, End = ex },
                    new SbValueCommand { Kind = SbCommandKind.VectorScaleY, StartMs = startMs, EndMs = endMs, Easing = easing, Start = sy, End = ey },
                ];
            }

            case "C":
            {
                RequireEither(f, 7, 10, lineNo, "C,easing,start,end,r1,g1,b1[,r2,g2,b2]");
                var start = new SbColor(ParseByte(f[4], lineNo), ParseByte(f[5], lineNo), ParseByte(f[6], lineNo));
                var end = f.Count > 7 ? new SbColor(ParseByte(f[7], lineNo), ParseByte(f[8], lineNo), ParseByte(f[9], lineNo)) : start;
                return [new SbColourCommand { StartMs = startMs, EndMs = endMs, Easing = easing, Start = start, End = end }];
            }

            case "P":
                RequireCount(f, 5, lineNo, "P,easing,start,end,flag");
                return [new SbFlagCommand { Kind = ParseFlag(f[4], lineNo), StartMs = startMs, EndMs = endMs, Easing = easing }];

            default:
                throw new OsbvParseException(lineNo, $"unknown command type \"{f[0]}\"");
        }
    }

    private static SbValueCommand ScalarCommand(SbCommandKind kind, List<string> f, int valueIndex, SbEasing easing, double startMs, double endMs, int lineNo)
    {
        RequireEither(f, valueIndex + 1, valueIndex + 2, lineNo, $"{f[0]},easing,start,end,startValue[,endValue]");
        var start = ParseFloat(f[valueIndex], lineNo);
        var end = f.Count > valueIndex + 1 ? ParseFloat(f[valueIndex + 1], lineNo) : start;
        return new SbValueCommand { Kind = kind, StartMs = startMs, EndMs = endMs, Easing = easing, Start = start, End = end };
    }

    private static SbCommandKind ParseFlag(string s, int lineNo) => s switch
    {
        "H" => SbCommandKind.FlipH,
        "V" => SbCommandKind.FlipV,
        "A" => SbCommandKind.Additive,
        _ => throw new OsbvParseException(lineNo, $"unknown P flag \"{s}\" (expected H, V, or A)"),
    };

    private static SbLayer ParseLayer(string s, int lineNo) =>
        Enum.TryParse<SbLayer>(s, out var v) ? v : throw new OsbvParseException(lineNo, $"unknown layer \"{s}\"");

    private static SbOrigin ParseOrigin(string s, int lineNo) =>
        Enum.TryParse<SbOrigin>(s, out var v) ? v : throw new OsbvParseException(lineNo, $"unknown origin \"{s}\"");

    private static double ParseDouble(string s, int lineNo) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new OsbvParseException(lineNo, $"expected number, got \"{s}\"");

    private static float ParseFloat(string s, int lineNo) => (float)ParseDouble(s, lineNo);

    private static int ParseInt(string s, int lineNo) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new OsbvParseException(lineNo, $"expected integer, got \"{s}\"");

    private static byte ParseByte(string s, int lineNo) =>
        byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new OsbvParseException(lineNo, $"expected byte 0-255, got \"{s}\"");

    private static void RequireCount(List<string> f, int count, int lineNo, string shape)
    {
        if (f.Count != count)
            throw new OsbvParseException(lineNo, $"expected {shape} ({count} fields), got {f.Count}");
    }

    private static void RequireEither(List<string> f, int a, int b, int lineNo, string shape)
    {
        if (f.Count != a && f.Count != b)
            throw new OsbvParseException(lineNo, $"expected {shape} ({a} or {b} fields), got {f.Count}");
    }

    /// <summary>Comma-splits a line, treating '"'-delimited spans as literal (commas inside
    /// stay part of the field) and stripping the quotes themselves — only the path field
    /// ever uses them.</summary>
    private static List<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (ch == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
