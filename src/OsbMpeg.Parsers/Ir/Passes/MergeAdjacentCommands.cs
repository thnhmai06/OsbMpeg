namespace OsbMpeg.Parsers.Ir.Passes;

/// <summary>
///     Collapses runs of same-kind SbValueCommand (Fade/MoveX/MoveY/Scale/
///     VectorScaleX/VectorScaleY/Rotate) that are time-adjacent, value-continuous, same easing,
///     AND collinear (identical slope on both sides of the join) into one command. Per-frame
///     baked output (GroupTransformBaker) is the main source of these — a group moving at
///     constant velocity samples to dozens of tiny linear segments that are all exactly
///     collinear, so this is a pure command-count win with zero effect on the evaluated curve
///     (CommandEvaluator's piecewise result at every timestamp is unchanged; merging only removes
///     redundant interior breakpoints). SbColourCommand/SbFlagCommand are left untouched —
///     SbColor's byte precision makes a slope check noisy for little payoff, and flag commands
///     are already minimally windowed by FlagWindows, not per-frame sampled.
/// </summary>
public static class MergeAdjacentCommands
{
    private const double TimeEpsilonMs = 0.01;
    private const float ValueEpsilon = 1e-4f;
    private const float SlopeEpsilon = 1e-4f;

    /// <summary>Mutates every object's Commands list in place.</summary>
    public static void Apply(SbDocument doc)
    {
        foreach (var obj in doc.AllObjects)
        {
            var merged = Merge(obj.Commands);
            obj.Commands.Clear();
            obj.Commands.AddRange(merged);
        }
    }

    public static List<SbCommand> Merge(List<SbCommand> commands)
    {
        var byKind = new Dictionary<SbCommandKind, List<SbValueCommand>>();
        var passthrough = new List<SbCommand>();

        foreach (var c in commands)
            if (c is SbValueCommand v)
            {
                if (!byKind.TryGetValue(v.Kind, out var list))
                    byKind[v.Kind] = list = [];
                list.Add(v);
            }
            else
            {
                passthrough.Add(c);
            }

        var result = new List<SbCommand>(passthrough);
        foreach (var list in byKind.Values)
        {
            list.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
            result.AddRange(MergeRun(list));
        }

        return result;
    }

    private static List<SbValueCommand> MergeRun(List<SbValueCommand> sorted)
    {
        var merged = new List<SbValueCommand>();
        foreach (var c in sorted)
            if (merged.Count > 0 && CanMerge(merged[^1], c))
            {
                var last = merged[^1];
                merged[^1] = new SbValueCommand
                {
                    Kind = last.Kind, Easing = last.Easing, StartMs = last.StartMs, EndMs = c.EndMs, Start = last.Start,
                    End = c.End
                };
            }
            else
            {
                merged.Add(c);
            }

        return merged;
    }

    private static bool CanMerge(SbValueCommand a, SbValueCommand b)
    {
        if (a.Easing != b.Easing) return false;
        if (!a.EndMs.IsEqual(b.StartMs, TimeEpsilonMs)) return false;
        if (!a.End.IsEqual(b.Start, ValueEpsilon)) return false;

        var durA = a.EndMs - a.StartMs;
        var durB = b.EndMs - b.StartMs;
        if (durA <= 0 || durB <= 0)
            return a.Start.IsEqual(a.End, ValueEpsilon) && b.Start.IsEqual(b.End, ValueEpsilon);

        var slopeA = (a.End - a.Start) / durA;
        var slopeB = (b.End - b.Start) / durB;
        return slopeA.IsEqual(slopeB, SlopeEpsilon);
    }
}