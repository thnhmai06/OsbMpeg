namespace OsbMpeg.Parsers.Ir;

/// <summary>
///     Expands SbLoop into plain absolute-time commands — .osb's L command replays its
///     children back-to-back, zero-based within each iteration (osu-wiki: "Events within a loop
///     should be timed with a zero-base... the loop event's start time is added at runtime").
///     Verified against osu.ppy.sh/wiki/en/Storyboard/Scripting/Compound_Commands: a loop starting
///     at 60000 with a 1000ms body (two F commands spanning 0-500 and 500-1000) repeats every
///     1000ms — i.e. iteration i's children fire at loopStart + i*cycleLength, where cycleLength
///     is the max EndMs across the (already-flattened) children, not a separately declared period.
///     Nested loops are flattened inside-out: a loop's own children are flattened first, so an
///     inner loop's full expanded duration correctly contributes to the outer loop's cycle length
///     — CommandEvaluator.Lifetime's simpler LoopSpan (which reads a child's raw EndMs, ignoring
///     that an SbLoop's own EndMs field is unused) is fine for the coarse "how long could this
///     object possibly be alive" bound it's used for, but not precise enough to reuse here.
/// </summary>
public static class LoopFlattener
{
    public static List<SbCommand> Flatten(List<SbCommand> commands)
    {
        var result = new List<SbCommand>();
        foreach (var c in commands)
        {
            if (c is not SbLoop loop)
            {
                result.Add(c);
                continue;
            }

            var flatChildren = Flatten(loop.Children);
            var cycleLength = (from fc in flatChildren where fc is not SbTrigger select fc.EndMs).Prepend(0.0).Max();

            var iterations = Math.Max(0, loop.Count - 1) + 1;
            for (var i = 0; i < iterations; i++)
            {
                var shift = loop.StartMs + i * cycleLength;
                result.AddRange(flatChildren.Select(fc => Shift(fc, shift)));
            }
        }

        return result;
    }

    private static SbCommand Shift(SbCommand c, double shift)
    {
        return c switch
        {
            SbValueCommand v => new SbValueCommand
            {
                Kind = v.Kind, Easing = v.Easing, StartMs = v.StartMs + shift, EndMs = v.EndMs + shift, Start = v.Start,
                End = v.End
            },
            SbColourCommand cc => new SbColourCommand
            {
                Easing = cc.Easing, StartMs = cc.StartMs + shift, EndMs = cc.EndMs + shift, Start = cc.Start,
                End = cc.End
            },
            SbFlagCommand f => new SbFlagCommand
                { Kind = f.Kind, Easing = f.Easing, StartMs = f.StartMs + shift, EndMs = f.EndMs + shift },
            SbTrigger => throw new NotSupportedException(
                "Trigger commands nested inside a Loop are not supported — a Loop iteration needs a fixed absolute start time to shift children by, but a Trigger fires at an unknown runtime event with no fixed instant."),
            _ => throw new NotSupportedException($"Unknown command type: {c.GetType()}")
        };
    }
}