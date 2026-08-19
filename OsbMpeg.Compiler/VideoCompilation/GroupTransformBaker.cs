using OsbMpeg.Ir;
using OsbMpeg.Render;

namespace OsbMpeg.VideoCompilation;

/// <summary>Bakes an AnimationVideo group's script into per-tile M/V/F/C/P,A commands — .osb
/// has no real group-transform object, so every tile sprite/animation the video decomposes
/// into must carry its own copy of the group's motion.
///
/// V1 scope: Move (M/MX/MY, absolute — matches CommandEvaluator's own default-to-declared-
/// position convention), Scale/VectorScale, Fade, Colour, Additive. Rotation and flip
/// (H/V) are rejected outright: rotation needs each tile's position AND its own R value to
/// track the group angle (a per-tile rigid-body decomposition), and flip needs each tile's
/// offset mirrored about the pivot plus its own P,H/V toggled to match — both correct but not
/// implemented yet, so refusing beats silently ignoring the script. Loop is rejected too:
/// CommandEvaluator doesn't walk into loop children for property evaluation, so baking one
/// would silently play back as if the loop's contents didn't exist.
///
/// Correctness over compactness: a tile run can span many frames while the group is still
/// moving, so a run's baked transform is either ONE static command set (when the group's
/// evaluated value doesn't change anywhere across the run's span — the common case, e.g. a
/// video that's simply placed and faded once) or one command set PER SAMPLED FRAME (when it
/// does) — sampled at the same fps the tile analysis already runs at, so this introduces no
/// error beyond the quantization the codec already accepts everywhere else. No polyline/chord-
/// error approximation is needed: that trick exists to compact a rotation into few commands,
/// and per-frame sampling makes the question moot for what's implemented here.</summary>
public sealed class GroupTransformBaker
{
    private readonly List<SbCommand> _group;
    private readonly float _pivotX;
    private readonly float _pivotY;
    private readonly double _frameDurationMs;

    public GroupTransformBaker(List<SbCommand> groupCommands, float pivotX, float pivotY, double fps)
    {
        if (groupCommands.Any(c => c is SbLoop))
            throw new NotSupportedException("AnimationVideo commands contain a Loop (\"L\") — group-transform baking doesn't evaluate loop children yet (V1). Flatten it or wait for baking support.");
        if (groupCommands.Any(c => c is SbValueCommand { Kind: SbCommandKind.Rotate }))
            throw new NotSupportedException("AnimationVideo commands contain Rotate (\"R\") — rotation baking isn't implemented yet (V1).");
        if (groupCommands.Any(c => c is SbFlagCommand { Kind: SbCommandKind.FlipH or SbCommandKind.FlipV }))
            throw new NotSupportedException("AnimationVideo commands contain a flip (\"P,H\"/\"P,V\") — flip-mirror baking isn't implemented yet (V1).");

        _group = groupCommands;
        _pivotX = pivotX;
        _pivotY = pivotY;
        _frameDurationMs = 1000.0 / fps;
    }

    /// <summary>baseCenterX/Y is the tile's un-transformed storyboard center (auto-cover
    /// mapping only, no script applied); baseScale is the mapping's StoryboardScale. Returns
    /// the commands to attach to a Centre-origin sprite/animation covering
    /// [localStartMs,localEndMs] (extraction-local time) at storyboardTimeOffsetMs.</summary>
    public (float X, float Y, List<SbCommand> Commands) Bake(double baseCenterX, double baseCenterY, double baseScale, double localStartMs, double localEndMs, double storyboardTimeOffsetMs)
    {
        var absStart = localStartMs + storyboardTimeOffsetMs;
        var absEnd = localEndMs + storyboardTimeOffsetMs;
        var first = SampleAt(absStart, baseCenterX, baseCenterY, baseScale);

        // Position depends on Move AND Scale/VectorScale (an off-pivot tile moves even with
        // no M command if the group is scaling), so all five must be constant together for
        // the tile's own position/size to be constant.
        var positionConstant = ScalarConstant(SbCommandKind.MoveX, _pivotX, absStart, absEnd)
            && ScalarConstant(SbCommandKind.MoveY, _pivotY, absStart, absEnd)
            && ScalarConstant(SbCommandKind.Scale, 1f, absStart, absEnd)
            && ScalarConstant(SbCommandKind.VectorScaleX, 1f, absStart, absEnd)
            && ScalarConstant(SbCommandKind.VectorScaleY, 1f, absStart, absEnd);
        var fadeConstant = ScalarConstant(SbCommandKind.Fade, 1f, absStart, absEnd);
        var colourConstant = ColourConstant(absStart, absEnd);
        var additiveConstant = FlagConstant(SbCommandKind.Additive, absStart, absEnd);

        var commands = new List<SbCommand>();

        if (positionConstant)
        {
            commands.Add(new SbValueCommand { Kind = SbCommandKind.MoveX, StartMs = absStart, EndMs = absEnd, Start = (float)first.X, End = (float)first.X });
            commands.Add(new SbValueCommand { Kind = SbCommandKind.MoveY, StartMs = absStart, EndMs = absEnd, Start = (float)first.Y, End = (float)first.Y });
            commands.Add(new SbValueCommand { Kind = SbCommandKind.VectorScaleX, StartMs = absStart, EndMs = absEnd, Start = (float)first.ScaleX, End = (float)first.ScaleX });
            commands.Add(new SbValueCommand { Kind = SbCommandKind.VectorScaleY, StartMs = absStart, EndMs = absEnd, Start = (float)first.ScaleY, End = (float)first.ScaleY });
        }
        if (fadeConstant && Math.Abs(first.Alpha - 1f) > 1e-4f)
            commands.Add(new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = absStart, EndMs = absEnd, Start = first.Alpha, End = first.Alpha });
        if (colourConstant && first.Colour != SbColor.White)
            commands.Add(new SbColourCommand { StartMs = absStart, EndMs = absEnd, Start = first.Colour, End = first.Colour });
        if (additiveConstant && first.Additive)
            commands.Add(new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = absStart, EndMs = absEnd });

        if (positionConstant && fadeConstant && colourConstant && additiveConstant)
            return ((float)first.X, (float)first.Y, commands); // fully static — the common case

        var samples = new List<(double T, TileSample S)> { (absStart, first) };
        for (var t = absStart + _frameDurationMs; t < absEnd; t += _frameDurationMs)
            samples.Add((t, SampleAt(t, baseCenterX, baseCenterY, baseScale)));
        samples.Add((absEnd, SampleAt(absEnd, baseCenterX, baseCenterY, baseScale)));

        for (var i = 0; i < samples.Count - 1; i++)
        {
            var (t0, a) = samples[i];
            var (t1, b) = samples[i + 1];
            if (!positionConstant)
            {
                commands.Add(new SbValueCommand { Kind = SbCommandKind.MoveX, StartMs = t0, EndMs = t1, Start = (float)a.X, End = (float)b.X });
                commands.Add(new SbValueCommand { Kind = SbCommandKind.MoveY, StartMs = t0, EndMs = t1, Start = (float)a.Y, End = (float)b.Y });
                commands.Add(new SbValueCommand { Kind = SbCommandKind.VectorScaleX, StartMs = t0, EndMs = t1, Start = (float)a.ScaleX, End = (float)b.ScaleX });
                commands.Add(new SbValueCommand { Kind = SbCommandKind.VectorScaleY, StartMs = t0, EndMs = t1, Start = (float)a.ScaleY, End = (float)b.ScaleY });
            }
            if (!fadeConstant)
                commands.Add(new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = t0, EndMs = t1, Start = a.Alpha, End = b.Alpha });
            if (!colourConstant)
                commands.Add(new SbColourCommand { StartMs = t0, EndMs = t1, Start = a.Colour, End = b.Colour });
        }

        if (!additiveConstant)
            commands.AddRange(AdditiveWindows(samples, absEnd));

        return ((float)first.X, (float)first.Y, commands);
    }

    /// <summary>P,A has no persistent "value" — StartMs==EndMs means permanent-from-here,
    /// otherwise it's active only during [StartMs,EndMs) and reverts after. So a true->false
    /// transition can't be expressed as a single boundary command (that would just turn it
    /// back on, permanently); each contiguous true run has to become its own
    /// [windowStart,windowEnd) span instead.</summary>
    private static IEnumerable<SbCommand> AdditiveWindows(List<(double T, TileSample S)> samples, double absEnd)
    {
        double? openStart = null;
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].S.Additive)
            {
                openStart ??= samples[i].T;
            }
            else if (openStart is { } start)
            {
                yield return new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = start, EndMs = samples[i].T };
                openStart = null;
            }
        }
        if (openStart is { } tailStart && absEnd > tailStart)
            yield return new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = tailStart, EndMs = absEnd };
    }

    private readonly record struct TileSample(double X, double Y, double ScaleX, double ScaleY, float Alpha, SbColor Colour, bool Additive);

    private TileSample SampleAt(double absT, double baseCenterX, double baseCenterY, double baseScale)
    {
        var moveX = CommandEvaluator.EvaluateScalar(_group, SbCommandKind.MoveX, absT, _pivotX);
        var moveY = CommandEvaluator.EvaluateScalar(_group, SbCommandKind.MoveY, absT, _pivotY);
        var scale = CommandEvaluator.EvaluateScalar(_group, SbCommandKind.Scale, absT, 1f);
        var vsx = CommandEvaluator.EvaluateScalar(_group, SbCommandKind.VectorScaleX, absT, 1f);
        var vsy = CommandEvaluator.EvaluateScalar(_group, SbCommandKind.VectorScaleY, absT, 1f);
        var sx = scale * vsx;
        var sy = scale * vsy;

        var x = moveX + (baseCenterX - _pivotX) * sx;
        var y = moveY + (baseCenterY - _pivotY) * sy;
        var alpha = CommandEvaluator.Flicker(CommandEvaluator.EvaluateScalar(_group, SbCommandKind.Fade, absT, 1f));
        var (r, g, b) = CommandEvaluator.EvaluateColour(_group, absT);
        var additive = CommandEvaluator.EvaluateFlag(_group, SbCommandKind.Additive, absT);

        return new TileSample(x, y, baseScale * sx, baseScale * sy, alpha, new SbColor(r, g, b), additive);
    }

    /// <summary>True iff this one property is unchanged across the whole [start,end] span:
    /// checked at both endpoints and at every relevant command's boundary that falls strictly
    /// inside the span. Since CommandEvaluator is piecewise-linear (piecewise-constant for
    /// flags) between consecutive command boundaries, matching values at every boundary plus
    /// the two endpoints proves the whole span is flat — a plain two-point sample would miss a
    /// command that moves out and back entirely within [start,end].</summary>
    private bool ScalarConstant(SbCommandKind kind, float defaultValue, double start, double end)
    {
        var s = CommandEvaluator.EvaluateScalar(_group, kind, start, defaultValue);
        if (CommandEvaluator.EvaluateScalar(_group, kind, end, defaultValue) != s)
            return false;

        foreach (var c in _group)
        {
            if (c is not SbValueCommand v || v.Kind != kind)
                continue;
            if (v.StartMs > start && v.StartMs < end && CommandEvaluator.EvaluateScalar(_group, kind, v.StartMs, defaultValue) != s)
                return false;
            if (v.EndMs > start && v.EndMs < end && CommandEvaluator.EvaluateScalar(_group, kind, v.EndMs, defaultValue) != s)
                return false;
        }
        return true;
    }

    private bool ColourConstant(double start, double end)
    {
        var s = CommandEvaluator.EvaluateColour(_group, start);
        if (CommandEvaluator.EvaluateColour(_group, end) != s)
            return false;

        foreach (var c in _group)
        {
            if (c is not SbColourCommand v)
                continue;
            if (v.StartMs > start && v.StartMs < end && CommandEvaluator.EvaluateColour(_group, v.StartMs) != s)
                return false;
            if (v.EndMs > start && v.EndMs < end && CommandEvaluator.EvaluateColour(_group, v.EndMs) != s)
                return false;
        }
        return true;
    }

    private bool FlagConstant(SbCommandKind kind, double start, double end)
    {
        var s = CommandEvaluator.EvaluateFlag(_group, kind, start);
        if (CommandEvaluator.EvaluateFlag(_group, kind, end) != s)
            return false;

        foreach (var c in _group)
        {
            if (c is not SbFlagCommand f || f.Kind != kind)
                continue;
            if (f.StartMs > start && f.StartMs < end && CommandEvaluator.EvaluateFlag(_group, kind, f.StartMs) != s)
                return false;
            if (f.EndMs > start && f.EndMs < end && CommandEvaluator.EvaluateFlag(_group, kind, f.EndMs) != s)
                return false;
        }
        return true;
    }
}
