using System.Globalization;
using OsbMpeg.Ir;

namespace OsbMpeg.Osb;

/// <summary>Serializes a <see cref="SbDocument"/> to .osb text. Always emits explicit
/// start/end time and value pairs — the shorthand omission forms (endtime-omitted,
/// value-omitted) are an optimizer-phase concern (ShorthandCompactor), not a writer concern.
/// The multi-value sequential shorthand is never emitted at all: lazer silently drops the
/// extra values, so writing it would produce a document that is valid text but wrong.</summary>
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
                w.WriteLine($"Animation,{obj.Layer},{obj.Origin},\"{a.Frames[0]}\",{FormatFloat(obj.X)},{FormatFloat(obj.Y)},{a.Frames.Length},{FormatFloat((float)a.FrameDelayMs)},{a.LoopType}");
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
                w.WriteLine($"{indent}V,{(int)p.X.Easing},{FormatTime(p.X.StartMs)},{FormatTime(p.X.EndMs)},{FormatFloat(p.X.Start)},{FormatFloat(p.Y.Start)},{FormatFloat(p.X.End)},{FormatFloat(p.Y.End)}");
                break;

            case SbValueCommand v:
                w.WriteLine($"{indent}{Acronym(v.Kind)},{(int)v.Easing},{FormatTime(v.StartMs)},{FormatTime(v.EndMs)},{FormatFloat(v.Start)},{FormatFloat(v.End)}");
                break;

            case SbColourCommand c:
                w.WriteLine($"{indent}C,{(int)c.Easing},{FormatTime(c.StartMs)},{FormatTime(c.EndMs)},{c.Start.R},{c.Start.G},{c.Start.B},{c.End.R},{c.End.G},{c.End.B}");
                break;

            case SbFlagCommand f:
                w.WriteLine($"{indent}P,{(int)f.Easing},{FormatTime(f.StartMs)},{FormatTime(f.EndMs)},{FlagLetter(f.Kind)}");
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
