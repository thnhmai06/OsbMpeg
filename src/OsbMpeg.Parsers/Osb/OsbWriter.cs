using System.Globalization;
using System.Linq;
using OsbMpeg.Ir;

namespace OsbMpeg.Osb;

/// <summary>Serializes a <see cref="SbDocument"/> to .osb text. Applies the two shorthand
/// omission forms the format actually supports (endtime field left blank when it equals
/// StartTime; end-value fields dropped entirely when they equal the start values) — both
/// decisions are self-contained per command line, so they live in the writer rather than as
/// a separate IR pass. The multi-value sequential shorthand is never emitted at all: lazer
/// silently drops the extra values, so writing it would produce a document that is valid
/// text but wrong.</summary>
public static class OsbWriter
{
    private static readonly SbLayer[] LayerOrder = [SbLayer.Background, SbLayer.Fail, SbLayer.Pass, SbLayer.Foreground, SbLayer.Overlay];

    public static void Write(SbDocument doc, string path)
    {
        using var w = new StreamWriter(path, append: false);
        w.WriteLine("[Events]");
        w.WriteLine("//Background and Video events");

        foreach (var layer in LayerOrder)
        {
            w.WriteLine($"//Storyboard Layer {(int)layer} ({layer})");
            if (!doc.Layers.TryGetValue(layer, out var objects))
                continue;

            foreach (var obj in objects)
            {
                if (!obj.HasCommands)
                    continue; // never instantiated by the renderer — don't write dead sprites

                WriteObjectHeader(w, obj);
                foreach (var cmd in NormalizeCommands(obj.Commands))
                    WriteCommand(w, cmd, depth: 1);
            }
        }

        w.WriteLine("//Storyboard Sound Samples");
    }

    private static void WriteObjectHeader(TextWriter w, SbObject obj)
    {
        switch (obj)
        {
            case SbSprite s:
                w.WriteLine($"Sprite,{obj.Layer},{obj.Origin},\"{s.Asset}\",{FormatFloat(obj.X)},{FormatFloat(obj.Y)}");
                break;
            case SbAnimation a:
                w.WriteLine($"Animation,{obj.Layer},{obj.Origin},\"{a.BasePath}\",{FormatFloat(obj.X)},{FormatFloat(obj.Y)},{a.FrameCount},{FormatFloat((float)a.FrameDelayMs)},{a.LoopType}");
                break;
            default:
                throw new NotSupportedException($"Unknown storyboard object type: {obj.GetType()}");
        }
    }

    /// <summary>Pairs VectorScaleX/VectorScaleY into a single V line (osu! has no VX/VY
    /// acronym — vector scale is always written as one command with both axes). Every
    /// other command kind maps to its own acronym independently (MX/MY are legal alone).</summary>
    private static IEnumerable<SbCommand> NormalizeCommands(List<SbCommand> commands)
    {
        var consumed = new HashSet<int>();
        for (var i = 0; i < commands.Count; i++)
        {
            if (consumed.Contains(i))
                continue;

            if (commands[i] is SbValueCommand { Kind: SbCommandKind.VectorScaleX } vx)
            {
                var j = commands.FindIndex(i + 1, c => c is SbValueCommand { Kind: SbCommandKind.VectorScaleY } vy
                    && vy.StartMs == vx.StartMs && vy.EndMs == vx.EndMs && vy.Easing == vx.Easing);
                if (j < 0)
                    throw new InvalidOperationException("VectorScaleX with no matching VectorScaleY at the same time span — osu! has no standalone VX/VY acronym.");
                consumed.Add(j);
                yield return new VectorPair(vx, (SbValueCommand)commands[j]);
                continue;
            }

            yield return commands[i];
        }
    }

    private sealed class VectorPair : SbCommand
    {
        public SbValueCommand X { get; }
        public SbValueCommand Y { get; }

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public VectorPair(SbValueCommand x, SbValueCommand y)
        {
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
            case VectorPair p:
                WriteCommandLine(w, indent, "V", p.X.Easing, p.X.StartMs, p.X.EndMs,
                    [FormatFloat(p.X.Start), FormatFloat(p.Y.Start)], [FormatFloat(p.X.End), FormatFloat(p.Y.End)]);
                break;

            case SbValueCommand v:
                WriteCommandLine(w, indent, Acronym(v.Kind), v.Easing, v.StartMs, v.EndMs, [FormatFloat(v.Start)], [FormatFloat(v.End)]);
                break;

            case SbColourCommand c:
                WriteCommandLine(w, indent, "C", c.Easing, c.StartMs, c.EndMs,
                    [c.Start.R.ToString(CultureInfo.InvariantCulture), c.Start.G.ToString(CultureInfo.InvariantCulture), c.Start.B.ToString(CultureInfo.InvariantCulture)],
                    [c.End.R.ToString(CultureInfo.InvariantCulture), c.End.G.ToString(CultureInfo.InvariantCulture), c.End.B.ToString(CultureInfo.InvariantCulture)]);
                break;

            case SbFlagCommand f:
                WriteCommandLine(w, indent, "P", f.Easing, f.StartMs, f.EndMs, [FlagLetter(f.Kind).ToString()], [FlagLetter(f.Kind).ToString()]);
                break;

            case SbLoop loop:
                w.WriteLine($"{indent}L,{FormatTime(loop.StartMs)},{loop.Count}");
                foreach (var child in NormalizeCommands(loop.Children))
                    WriteCommand(w, child, depth: 2);
                break;

            case SbTrigger trigger:
                w.WriteLine($"{indent}T,{trigger.Name},{FormatTime(trigger.StartMs)},{FormatTime(trigger.EndMs)},{trigger.Group}");
                foreach (var child in NormalizeCommands(trigger.Children))
                    WriteCommand(w, child, depth: 2);
                break;

            default:
                throw new NotSupportedException($"Unknown command type: {cmd.GetType()}");
        }
    }

    /// <summary>Writes one command line with both shorthand omissions applied: the endtime
    /// field is left blank when it equals StartTime, and endValues is dropped entirely when
    /// it's identical (field-for-field, as formatted text) to startValues.</summary>
    private static void WriteCommandLine(TextWriter w, string indent, string acronym, SbEasing easing, double startMs, double endMs, string[] startValues, string[] endValues)
    {
        var startTime = FormatTime(startMs);
        var endTime = startTime == FormatTime(endMs) ? "" : FormatTime(endMs);
        var fields = new List<string> { acronym, ((int)easing).ToString(CultureInfo.InvariantCulture), startTime, endTime };
        fields.AddRange(startValues);
        if (!startValues.SequenceEqual(endValues))
            fields.AddRange(endValues);
        w.WriteLine($"{indent}{string.Join(",", fields)}");
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

    private static string FormatTime(double ms) => ((long)Math.Round(ms, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    private static string FormatFloat(float value) => value.ToString(CultureInfo.InvariantCulture);
}
