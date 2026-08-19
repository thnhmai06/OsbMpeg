namespace OsbMpeg.Render;

/// <summary>Command-persistence evaluation shared by SoftwareStoryboardRenderer (Compiler)
/// and the AnimationVideo group-transform baker (Compiler): given a command list and a query
/// time t, what's the value of a scalar/colour/flag property right now? Pulled out to Parsers
/// so both consumers — rendering an .osb for playback/benchmarking, and baking a video
/// group's script into per-tile sprites — evaluate identically instead of two copies drifting
/// apart. Pure IR math, no rendering (ImageSharp/Compositor) dependency.
///
/// Semantics (verified against ppy/osu's LegacyStoryboardDecoder / DrawableStoryboard*):
/// before the first command's start, hold that command's start value; after the last
/// command's end, hold its end value; between two commands of the same kind, hold the
/// nearer-preceding one's end value. P (flag) commands are the one exception — StartMs==EndMs
/// means "permanent from here on", otherwise active only during [StartMs,EndMs).</summary>
public static class CommandEvaluator
{
    public static float EvaluateScalar(List<Ir.SbCommand> commands, Ir.SbCommandKind kind, double t, float defaultValue)
    {
        Ir.SbValueCommand? active = null;
        Ir.SbValueCommand? last = null;
        Ir.SbValueCommand? first = null;

        foreach (var c in commands)
        {
            if (c is not Ir.SbValueCommand v || v.Kind != kind)
                continue;
            first ??= v;
            if (t >= v.StartMs && t <= v.EndMs)
                active = v;
            if (last is null || v.EndMs >= last.EndMs)
                last = v;
        }

        if (first is null)
            return defaultValue;
        if (active is not null)
        {
            if (active.StartMs == active.EndMs)
                return active.End;
            var p = (t - active.StartMs) / (active.EndMs - active.StartMs);
            return Lerp(active.Start, active.End, (float)EasingTable.Apply(active.Easing, p));
        }

        return t < first.StartMs ? first.Start : last!.End;
    }

    public static (byte R, byte G, byte B) EvaluateColour(List<Ir.SbCommand> commands, double t)
    {
        Ir.SbColourCommand? active = null;
        Ir.SbColourCommand? last = null;
        Ir.SbColourCommand? first = null;

        foreach (var c in commands)
        {
            if (c is not Ir.SbColourCommand v)
                continue;
            first ??= v;
            if (t >= v.StartMs && t <= v.EndMs)
                active = v;
            if (last is null || v.EndMs >= last.EndMs)
                last = v;
        }

        if (first is null)
            return (Ir.SbColor.White.R, Ir.SbColor.White.G, Ir.SbColor.White.B);

        if (active is not null)
        {
            if (active.StartMs == active.EndMs)
                return (active.End.R, active.End.G, active.End.B);
            var p = (float)EasingTable.Apply(active.Easing, (t - active.StartMs) / (active.EndMs - active.StartMs));
            return (
                (byte)Lerp(active.Start.R, active.End.R, p),
                (byte)Lerp(active.Start.G, active.End.G, p),
                (byte)Lerp(active.Start.B, active.End.B, p));
        }

        var c2 = t < first.StartMs ? first.Start : last!.End;
        return (c2.R, c2.G, c2.B);
    }

    /// <summary>P command semantics: StartMs==EndMs makes it permanent from that point on
    /// (never reverts); otherwise it is only active during [StartMs,EndMs).</summary>
    public static bool EvaluateFlag(List<Ir.SbCommand> commands, Ir.SbCommandKind kind, double t)
    {
        foreach (var c in commands)
        {
            if (c is not Ir.SbFlagCommand f || f.Kind != kind)
                continue;
            if (f.StartMs == f.EndMs ? t >= f.StartMs : t >= f.StartMs && t < f.EndMs)
                return true;
        }
        return false;
    }

    /// <summary>Active span: earliest start to latest end across all non-trigger top-level
    /// commands (matches EarliestTransformTime / EndTimeForDisplay). Loop spans use the loop's
    /// own start plus (max relative child end) * effective iteration count, min 1 iteration.</summary>
    public static (double Start, double End) Lifetime(List<Ir.SbCommand> commands)
    {
        var start = double.MaxValue;
        var end = double.MinValue;

        foreach (var c in commands)
        {
            if (c is Ir.SbTrigger)
                continue;

            var (s, e) = c is Ir.SbLoop loop ? LoopSpan(loop) : (c.StartMs, c.EndMs);
            start = Math.Min(start, s);
            end = Math.Max(end, e);
        }

        return start == double.MaxValue ? (0, 0) : (start, end);
    }

    private static (double Start, double End) LoopSpan(Ir.SbLoop loop)
    {
        var childEnd = 0.0;
        foreach (var c in loop.Children)
        {
            if (c is Ir.SbTrigger)
                continue;
            childEnd = Math.Max(childEnd, c.EndMs);
        }

        var iterations = Math.Max(0, loop.Count - 1) + 1;
        return (loop.StartMs, loop.StartMs + childEnd * iterations);
    }

    /// <summary>lazer reproduces a stable exploit where alpha values above 1 wrap instead of
    /// clamping, used deliberately by storyboarders for flicker effects.</summary>
    public static float Flicker(float alpha) => alpha > 1 ? alpha % 1 : alpha;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
