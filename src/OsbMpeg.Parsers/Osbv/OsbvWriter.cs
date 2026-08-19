using System.Globalization;
using OsbMpeg.Ir;

namespace OsbMpeg.Osbv;

/// <summary>Serializes an <see cref="OsbvDocument"/> back to .osbv text. Exists to give the
/// parser a round-trip self-check (parse → write → parse again → structurally equal) — it
/// is not part of the compiler pipeline, which consumes the AST directly. Unlike OsbWriter
/// (which always emits MX/MY and VX/VY separately, matching how the compiler's own analysis
/// produces them), this pairs MoveX+MoveY back into "M" and VectorScaleX+VectorScaleY back
/// into "V" whenever they share timing/easing, so text a human wrote as one M/V line reads
/// back the same way instead of split in two.</summary>
public static class OsbvWriter
{
    public static void Write(OsbvDocument doc, string path)
    {
        using var w = new StreamWriter(path, append: false);
        Write(doc, w);
    }

    public static string WriteToString(OsbvDocument doc)
    {
        var sw = new StringWriter();
        Write(doc, sw);
        return sw.ToString();
    }

    private static void Write(OsbvDocument doc, TextWriter w)
    {
        w.WriteLine("OsbV: 1");
        foreach (var obj in doc.Objects)
        {
            WriteHeader(w, obj);
            foreach (var cmd in Pair(obj.Commands))
                WriteCommand(w, cmd, depth: 1);
        }
    }

    private static void WriteHeader(TextWriter w, OsbvObject obj)
    {
        switch (obj)
        {
            case OsbvSprite s:
                w.WriteLine($"Sprite,{obj.Layer},{obj.Origin},\"{s.FilePath}\",{F(obj.X)},{F(obj.Y)}");
                break;
            case OsbvAnimation a:
                w.WriteLine($"Animation,{obj.Layer},{obj.Origin},\"{a.FilePath}\",{F(obj.X)},{F(obj.Y)},{a.FrameCount},{F(a.FrameDelayMs)},{a.LoopType}");
                break;
            case OsbvAnimationVideo v:
            {
                var fields = new List<string> { "AnimationVideo", obj.Layer.ToString(), obj.Origin.ToString(), $"\"{v.FilePath}\"", F(obj.X), F(obj.Y), F(v.StartTimeMs) };
                if (v.Fps is { } fps) fields.Add(F(fps));
                if (v.VideoStartMs is { } vs) fields.Add(F(vs));
                if (v.VideoEndMs is { } ve) fields.Add(F(ve));
                w.WriteLine(string.Join(',', fields));
                break;
            }
        }
    }

    /// <summary>Re-pairs MoveX+MoveY (and VectorScaleX+VectorScaleY) that share
    /// StartMs/EndMs/Easing back into one M/V command, mirroring OsbvParser's expansion in
    /// reverse. Anything left unpaired stays as its own MX/MY/S/etc. line.</summary>
    private static IEnumerable<SbCommand> Pair(List<SbCommand> commands)
    {
        var consumed = new HashSet<int>();
        for (var i = 0; i < commands.Count; i++)
        {
            if (consumed.Contains(i))
                continue;

            if (commands[i] is SbValueCommand { Kind: SbCommandKind.MoveX } mx)
            {
                var j = FindMatch(commands, i, mx, SbCommandKind.MoveY);
                if (j >= 0) { consumed.Add(j); yield return new Pair2("M", mx, (SbValueCommand)commands[j]); continue; }
            }
            if (commands[i] is SbValueCommand { Kind: SbCommandKind.VectorScaleX } vx)
            {
                var j = FindMatch(commands, i, vx, SbCommandKind.VectorScaleY);
                if (j >= 0) { consumed.Add(j); yield return new Pair2("V", vx, (SbValueCommand)commands[j]); continue; }
            }

            yield return commands[i];
        }
    }

    private static int FindMatch(List<SbCommand> commands, int i, SbValueCommand a, SbCommandKind pairKind) =>
        commands.FindIndex(i + 1, c => c is SbValueCommand v && v.Kind == pairKind && v.StartMs == a.StartMs && v.EndMs == a.EndMs && v.Easing == a.Easing);

    private sealed class Pair2 : SbCommand
    {
        public string Acronym { get; }
        public SbValueCommand X { get; }
        public SbValueCommand Y { get; }

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public Pair2(string acronym, SbValueCommand x, SbValueCommand y)
        {
            Acronym = acronym;
            X = x;
            Y = y;
            StartMs = x.StartMs;
            EndMs = x.EndMs;
            Easing = x.Easing;
        }
    }

    private static void WriteCommand(TextWriter w, SbCommand cmd, int depth)
    {
        var indent = new string(' ', depth);
        switch (cmd)
        {
            case Pair2 p:
                w.WriteLine($"{indent}{p.Acronym},{(int)p.X.Easing},{T(p.X.StartMs)},{T(p.X.EndMs)},{FF(p.X.Start)},{FF(p.Y.Start)},{FF(p.X.End)},{FF(p.Y.End)}");
                break;

            case SbValueCommand v:
                w.WriteLine($"{indent}{Acronym(v.Kind)},{(int)v.Easing},{T(v.StartMs)},{T(v.EndMs)},{FF(v.Start)},{FF(v.End)}");
                break;

            case SbColourCommand c:
                w.WriteLine($"{indent}C,{(int)c.Easing},{T(c.StartMs)},{T(c.EndMs)},{c.Start.R},{c.Start.G},{c.Start.B},{c.End.R},{c.End.G},{c.End.B}");
                break;

            case SbFlagCommand f:
                w.WriteLine($"{indent}P,{(int)f.Easing},{T(f.StartMs)},{T(f.EndMs)},{FlagLetter(f.Kind)}");
                break;

            case SbLoop loop:
                w.WriteLine($"{indent}L,{T(loop.StartMs)},{loop.Count}");
                foreach (var child in Pair(loop.Children))
                    WriteCommand(w, child, depth + 1);
                break;

            case SbTrigger trigger:
                w.WriteLine($"{indent}T,{trigger.Name},{T(trigger.StartMs)},{T(trigger.EndMs)},{trigger.Group}");
                foreach (var child in Pair(trigger.Children))
                    WriteCommand(w, child, depth + 1);
                break;

            default:
                throw new NotSupportedException($"Unknown command type: {cmd.GetType()}");
        }
    }

    private static string Acronym(SbCommandKind kind) => kind switch
    {
        SbCommandKind.Fade => "F",
        SbCommandKind.MoveX => "MX",
        SbCommandKind.MoveY => "MY",
        SbCommandKind.Scale => "S",
        SbCommandKind.Rotate => "R",
        _ => throw new NotSupportedException($"{kind} has no standalone acronym (paired command?)"),
    };

    private static char FlagLetter(SbCommandKind kind) => kind switch
    {
        SbCommandKind.FlipH => 'H',
        SbCommandKind.FlipV => 'V',
        SbCommandKind.Additive => 'A',
        _ => throw new NotSupportedException($"{kind} is not a flag command"),
    };

    private static string T(double ms) => ((long)Math.Round(ms, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
    private static string F(double value) => value.ToString(CultureInfo.InvariantCulture);
    private static string FF(float value) => value.ToString(CultureInfo.InvariantCulture);
}
