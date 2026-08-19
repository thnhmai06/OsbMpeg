using OsbMpeg.Ir;
using OsbMpeg.Render;

namespace OsbMpeg.VideoCompilation;

/// <summary>Bakes an AnimationVideo group's script into per-tile M/V/R/F/C/P commands — .osb
/// has no real group-transform object, so every tile sprite/animation the video decomposes
/// into must carry its own copy of the group's motion.
///
/// V1 scope: Move (M/MX/MY, absolute — matches CommandEvaluator's own default-to-declared-
/// position convention), Scale/VectorScale, Rotate, Fade, Colour, Additive, flip (P,H/P,V).
/// Loop is rejected: CommandEvaluator doesn't walk into loop children for property
/// evaluation, so baking one would silently play back as if the loop's contents didn't exist.
///
/// Rotation and flip are a per-tile rigid-body decomposition, not just a copied value: a
/// tile's CENTER has to move along the same arc/mirror the group's whole rotation/flip
/// traces, and the tile ALSO gets its own matching R / P,H / P,V so its own content is
/// oriented correctly — same composition Compositor.Blit already applies to a single object
/// (flip sign folded into scale, applied before the rotation matrix), reused here for
/// consistency: offset = (baseCenter - pivot) * scale * (flipped ? -1 : 1), then rotated by
/// R(theta) using the identical [cos -sin; sin cos] matrix Compositor uses for pixel sampling.
/// Known gap: two adjacent tiles round their own baked X/Y independently, so a rotated seam
/// between them isn't guaranteed sub-pixel-tight (untested — no pixel-level seam check yet).
///
/// Correctness over compactness: a tile run can span many frames while the group is still
/// moving, so a run's baked transform is either ONE static command set (when the group's
/// evaluated value doesn't change anywhere across the run's span — the common case, e.g. a
/// video that's simply placed and faded once) or one command set PER SAMPLED FRAME (when it
/// does) — sampled at the same fps the tile analysis already runs at, so this introduces no
/// error beyond the quantization the codec already accepts everywhere else. No polyline/chord-
/// error approximation is needed for rotation: that trick exists to compact a rotation into
/// few commands, and per-frame sampling makes the question moot.</summary>
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

        // Position depends on Move, Scale/VectorScale, Rotate, AND flip (an off-pivot tile
        // moves even with no M command if the group rotates, scales, or mirrors), so all of
        // them must be constant together for the tile's own position/size/orientation to be
        // constant.
        var positionConstant = ScalarConstant(SbCommandKind.MoveX, _pivotX, absStart, absEnd)
            && ScalarConstant(SbCommandKind.MoveY, _pivotY, absStart, absEnd)
            && ScalarConstant(SbCommandKind.Scale, 1f, absStart, absEnd)
            && ScalarConstant(SbCommandKind.VectorScaleX, 1f, absStart, absEnd)
            && ScalarConstant(SbCommandKind.VectorScaleY, 1f, absStart, absEnd)
            && ScalarConstant(SbCommandKind.Rotate, 0f, absStart, absEnd)
            && FlagConstant(SbCommandKind.FlipH, absStart, absEnd)
            && FlagConstant(SbCommandKind.FlipV, absStart, absEnd);
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
            if (Math.Abs(first.Rotation) > 1e-6)
                commands.Add(new SbValueCommand { Kind = SbCommandKind.Rotate, StartMs = absStart, EndMs = absEnd, Start = (float)first.Rotation, End = (float)first.Rotation });
            if (first.FlipH)
                commands.Add(new SbFlagCommand { Kind = SbCommandKind.FlipH, StartMs = absStart, EndMs = absEnd });
            if (first.FlipV)
                commands.Add(new SbFlagCommand { Kind = SbCommandKind.FlipV, StartMs = absStart, EndMs = absEnd });
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
                commands.Add(new SbValueCommand { Kind = SbCommandKind.Rotate, StartMs = t0, EndMs = t1, Start = (float)a.Rotation, End = (float)b.Rotation });
            }
            if (!fadeConstant)
                commands.Add(new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = t0, EndMs = t1, Start = a.Alpha, End = b.Alpha });
            if (!colourConstant)
                commands.Add(new SbColourCommand { StartMs = t0, EndMs = t1, Start = a.Colour, End = b.Colour });
        }

        if (!additiveConstant)
            commands.AddRange(FlagWindows(SbCommandKind.Additive, samples, absEnd, s => s.Additive));
        // FlipH/FlipV are bundled into positionConstant above, so !positionConstant doesn't by
        // itself mean they changed — check independently so a tile whose flip stays fixed
        // while only e.g. Move animates still gets one static P,H/V instead of a redundant window.
        if (!positionConstant)
        {
            if (FlagConstant(SbCommandKind.FlipH, absStart, absEnd)) { if (first.FlipH) commands.Add(new SbFlagCommand { Kind = SbCommandKind.FlipH, StartMs = absStart, EndMs = absEnd }); }
            else commands.AddRange(FlagWindows(SbCommandKind.FlipH, samples, absEnd, s => s.FlipH));

            if (FlagConstant(SbCommandKind.FlipV, absStart, absEnd)) { if (first.FlipV) commands.Add(new SbFlagCommand { Kind = SbCommandKind.FlipV, StartMs = absStart, EndMs = absEnd }); }
            else commands.AddRange(FlagWindows(SbCommandKind.FlipV, samples, absEnd, s => s.FlipV));
        }

        return ((float)first.X, (float)first.Y, commands);
    }

    /// <summary>P (flag) commands have no persistent "value" — StartMs==EndMs means
    /// permanent-from-here, otherwise active only during [StartMs,EndMs) and reverting after.
    /// So a true->false transition can't be a single boundary command (that would just turn it
    /// back on, permanently); each contiguous true run has to become its own
    /// [windowStart,windowEnd) span instead.</summary>
    private static IEnumerable<SbCommand> FlagWindows(SbCommandKind kind, List<(double T, TileSample S)> samples, double absEnd, Func<TileSample, bool> select)
    {
        double? openStart = null;
        for (var i = 0; i < samples.Count; i++)
        {
            if (select(samples[i].S))
            {
                openStart ??= samples[i].T;
            }
            else if (openStart is { } start)
            {
                yield return new SbFlagCommand { Kind = kind, StartMs = start, EndMs = samples[i].T };
                openStart = null;
            }
        }
        if (openStart is { } tailStart && absEnd > tailStart)
            yield return new SbFlagCommand { Kind = kind, StartMs = tailStart, EndMs = absEnd };
    }

    private readonly record struct TileSample(double X, double Y, double ScaleX, double ScaleY, double Rotation, bool FlipH, bool FlipV, float Alpha, SbColor Colour, bool Additive);

    private TileSample SampleAt(double absT, double baseCenterX, double baseCenterY, double baseScale)
    {
        var moveX = CommandEvaluator.EvaluateScalar(_group, SbCommandKind.MoveX, absT, _pivotX);
        var moveY = CommandEvaluator.EvaluateScalar(_group, SbCommandKind.MoveY, absT, _pivotY);
        var scale = CommandEvaluator.EvaluateScalar(_group, SbCommandKind.Scale, absT, 1f);
        var vsx = CommandEvaluator.EvaluateScalar(_group, SbCommandKind.VectorScaleX, absT, 1f);
        var vsy = CommandEvaluator.EvaluateScalar(_group, SbCommandKind.VectorScaleY, absT, 1f);
        var sx = scale * vsx;
        var sy = scale * vsy;
        var flipH = CommandEvaluator.EvaluateFlag(_group, SbCommandKind.FlipH, absT);
        var flipV = CommandEvaluator.EvaluateFlag(_group, SbCommandKind.FlipV, absT);
        var theta = (double)CommandEvaluator.EvaluateScalar(_group, SbCommandKind.Rotate, absT, 0f);

        // Same composition Compositor.Blit uses for a single object's own pixel sampling:
        // flip folded into scale sign first (in the group's unrotated local frame), then the
        // [cos -sin; sin cos] rotation matrix.
        var offX = (baseCenterX - _pivotX) * sx * (flipH ? -1 : 1);
        var offY = (baseCenterY - _pivotY) * sy * (flipV ? -1 : 1);
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);
        var rotX = offX * cos - offY * sin;
        var rotY = offX * sin + offY * cos;

        var x = moveX + rotX;
        var y = moveY + rotY;
        var alpha = CommandEvaluator.Flicker(CommandEvaluator.EvaluateScalar(_group, SbCommandKind.Fade, absT, 1f));
        var (r, g, b) = CommandEvaluator.EvaluateColour(_group, absT);
        var additive = CommandEvaluator.EvaluateFlag(_group, SbCommandKind.Additive, absT);

        return new TileSample(x, y, baseScale * sx, baseScale * sy, theta, flipH, flipV, alpha, new SbColor(r, g, b), additive);
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
