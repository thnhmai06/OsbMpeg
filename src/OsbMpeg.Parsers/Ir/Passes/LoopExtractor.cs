namespace OsbMpeg.Parsers.Ir.Passes;

/// <summary>
///     Inverse of <see cref="LoopFlattener" />: detects a maximal run of same-Kind
///     SbValueCommand (already grouped/sorted by <see cref="MergeAdjacentCommands" />) whose values
///     repeat identically across a constant cycle length, and re-wraps it into a single SbLoop.
///     Targets GroupTransformBaker's baked output — baking evaluates a .osbv script frame-by-frame
///     and has no notion of the script's own L constructs, so a genuinely periodic motion (e.g. a
///     looped back-and-forth move) comes out flat and gets re-detected here.
///     Scope matches MergeAdjacentCommands: only SbValueCommand groups. SbColourCommand/
///     SbFlagCommand untouched — a flag command's permanent-vs-revert asymmetry (StartMs==EndMs
///     means "forever") makes loop-wrapping risky, and colour bytes are low value.
///     Win is .osb text bytes only, not asset bytes — same commands, written once instead of N
///     times.
/// </summary>
public static class LoopExtractor
{
    private const int MinIterations = 3;
    private const double TimeEpsilonMs = 0.01;
    private const float ValueEpsilon = 1e-4f;

    /// <summary>Mutates every object's Commands list in place.</summary>
    public static void Apply(SbDocument doc)
    {
        foreach (var obj in doc.AllObjects)
        {
            var extracted = Extract(obj.Commands);
            obj.Commands.Clear();
            obj.Commands.AddRange(extracted);
        }
    }

    public static List<SbCommand> Extract(List<SbCommand> commands)
    {
        var result = new List<SbCommand>();
        var i = 0;
        while (i < commands.Count)
        {
            if (commands[i] is not SbValueCommand first)
            {
                result.Add(commands[i]);
                i++;
                continue;
            }

            // Maximal contiguous same-Kind block starting at i — matches how
            // MergeAdjacentCommands groups its output, so this is the natural unit to scan.
            var end = i + 1;
            while (end < commands.Count && commands[end] is SbValueCommand v && v.Kind == first.Kind)
                end++;

            result.AddRange(ExtractBlock(commands.GetRange(i, end - i)));
            i = end;
        }

        return result;
    }

    private static List<SbCommand> ExtractBlock(List<SbCommand> block)
    {
        var values = block.Cast<SbValueCommand>().ToList();

        for (var period = 1; period <= values.Count / MinIterations; period++)
        {
            if (values.Count % period != 0) continue;
            var iterations = values.Count / period;
            if (iterations < MinIterations) continue;

            var loopStart = values[0].StartMs;
            // L's replay cadence is entirely determined by the body's own span (see
            // LoopFlattener: cycleLength = max EndMs across the flattened children) — there is no
            // independent "gap between iterations" in the format. A candidate is only a real loop
            // when the observed start-to-start gap matches that body span exactly; otherwise
            // wrapping it in SbLoop would flatten back to a different (wrong) cadence.
            var bodyCycleLength = values.Take(period).Max(v => v.EndMs) - loopStart;
            var observedGap = values[period].StartMs - loopStart;
            if (bodyCycleLength <= 0 || !bodyCycleLength.IsEqual(observedGap, TimeEpsilonMs)) continue;
            if (!MatchesPeriod(values, period, iterations, bodyCycleLength)) continue;

            List<SbCommand> children =
            [
                .. values.Take(period).Select(v => new SbValueCommand
                {
                    Kind = v.Kind, Easing = v.Easing, StartMs = v.StartMs - loopStart,
                    EndMs = v.EndMs - loopStart, Start = v.Start, End = v.End
                })
            ];

            return [new SbLoop { StartMs = loopStart, EndMs = loopStart, Count = iterations, Children = children }];
        }

        return [.. block];
    }

    /// <summary>
    ///     True when every iteration after the first reproduces the first iteration's
    ///     values exactly, shifted by exactly <paramref name="cycleLength" /> per iteration — a
    ///     coincidental value match at the wrong cadence must not count as a loop.
    /// </summary>
    private static bool MatchesPeriod(List<SbValueCommand> values, int period, int iterations, double cycleLength)
    {
        for (var it = 1; it < iterations; it++)
        for (var j = 0; j < period; j++)
        {
            var baseCmd = values[j];
            var candidate = values[it * period + j];
            var shift = it * cycleLength;

            if (candidate.Easing != baseCmd.Easing) return false;
            if (!candidate.StartMs.IsEqual(baseCmd.StartMs + shift, TimeEpsilonMs)) return false;
            if (!candidate.EndMs.IsEqual(baseCmd.EndMs + shift, TimeEpsilonMs)) return false;
            if (!candidate.Start.IsEqual(baseCmd.Start, ValueEpsilon)) return false;
            if (!candidate.End.IsEqual(baseCmd.End, ValueEpsilon)) return false;
        }

        return true;
    }
}
