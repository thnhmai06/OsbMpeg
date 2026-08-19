using OsbMpeg.Ir;
using OsbMpeg.VideoCompilation;
using Xunit;

namespace OsbMpeg.Tests;

public class GroupTransformBakerTests
{
    private static List<SbCommand> Fade(double start, double end, float from, float to) =>
        [new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = start, EndMs = end, Start = from, End = to }];

    [Fact]
    public void ConstantSpanAfterFade_EmitsOneStaticCommandSet_NoFadeOrColour()
    {
        var baker = new GroupTransformBaker(Fade(0, 2000, 0, 1), pivotX: 320, pivotY: 240, fps: 30);

        // run [3000,4000] is entirely after the fade ends -> alpha is constantly 1 there,
        // and colour/additive were never touched at all -> cheap static path, no F/C/P lines.
        var (x, y, commands) = baker.Bake(baseCenterX: 320, baseCenterY: 240, baseScale: 1, localStartMs: 3000, localEndMs: 4000, storyboardTimeOffsetMs: 0);

        Assert.Equal(320, x);
        Assert.Equal(240, y);
        Assert.Equal(4, commands.Count); // MoveX, MoveY, VectorScaleX, VectorScaleY only
        Assert.All(commands, c => Assert.Equal(3000, c.StartMs));
        Assert.All(commands, c => Assert.Equal(4000, c.EndMs));
    }

    [Fact]
    public void SpanOverlappingFade_SamplesPerFrame_FirstSampleMatchesFadeStart()
    {
        var baker = new GroupTransformBaker(Fade(0, 2000, 0, 1), pivotX: 320, pivotY: 240, fps: 30);

        var (_, _, commands) = baker.Bake(baseCenterX: 320, baseCenterY: 240, baseScale: 1, localStartMs: 0, localEndMs: 2000, storyboardTimeOffsetMs: 0);

        Assert.True(commands.Count > 4, "a span overlapping an active fade must not take the static 4-command path");
        var firstFade = Assert.IsType<SbValueCommand>(commands.First(c => c is SbValueCommand { Kind: SbCommandKind.Fade }));
        Assert.Equal(0f, firstFade.Start);
    }

    [Fact]
    public void OffsetTile_CentersCorrectly_UnderUniformScale()
    {
        // No commands beyond a no-op far-future fade -> pure geometry check.
        var baker = new GroupTransformBaker(Fade(0, 1, 1, 1), pivotX: 320, pivotY: 240, fps: 30);

        // A tile whose base (un-transformed) center sits 100 storyboard units right of the
        // pivot, under baseScale=0.5: with no Move/Scale commands the group holds at its
        // declared pivot, so the tile's position is pivot + (baseCenter-pivot)*scale = 320+100*1=420
        // (Scale/VectorScale default to 1, independent of baseScale which only affects the
        // tile's own size, not the position formula's scale factor here).
        var (x, y, _) = baker.Bake(baseCenterX: 420, baseCenterY: 240, baseScale: 0.5, localStartMs: 5000, localEndMs: 6000, storyboardTimeOffsetMs: 0);

        Assert.Equal(420, x, precision: 3);
        Assert.Equal(240, y, precision: 3);
    }

    [Fact]
    public void AdditiveTrueFalseTrue_EmitsTwoRevertingSpans_NotPermanentOn()
    {
        List<SbCommand> group =
        [
            new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = 0, EndMs = 500 },
            new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = 1000, EndMs = 1500 },
        ];
        var baker = new GroupTransformBaker(group, pivotX: 320, pivotY: 240, fps: 30);

        var (_, _, commands) = baker.Bake(baseCenterX: 320, baseCenterY: 240, baseScale: 1, localStartMs: 0, localEndMs: 2000, storyboardTimeOffsetMs: 0);

        var flags = commands.OfType<SbFlagCommand>().Where(f => f.Kind == SbCommandKind.Additive).ToList();
        Assert.Equal(2, flags.Count);
        Assert.All(flags, f => Assert.True(f.EndMs > f.StartMs, "each window must revert (StartMs==EndMs would mean permanent-on)"));
    }

    [Fact]
    public void ConstantQuarterTurn_RotatesOffsetIntoCorrectQuadrant()
    {
        // theta = pi/2 permanently (StartMs==EndMs). An offset tile 100 units right of the
        // pivot (baseCenterX=420 vs pivotX=320) must swing to 100 units BELOW the pivot,
        // matching Compositor's [cos -sin; sin cos] convention: rotX = offX*cos - offY*sin,
        // rotY = offX*sin + offY*cos; with offX=100, offY=0, theta=pi/2 -> (0, 100).
        List<SbCommand> group = [new SbValueCommand { Kind = SbCommandKind.Rotate, StartMs = 0, EndMs = 0, Start = (float)(Math.PI / 2), End = (float)(Math.PI / 2) }];
        var baker = new GroupTransformBaker(group, pivotX: 320, pivotY: 240, fps: 30);

        var (x, y, commands) = baker.Bake(baseCenterX: 420, baseCenterY: 240, baseScale: 1, localStartMs: 1000, localEndMs: 2000, storyboardTimeOffsetMs: 0);

        Assert.Equal(320, x, precision: 3);
        Assert.Equal(340, y, precision: 3); // pivotY(240) + rotY(100)
        Assert.Contains(commands, c => c is SbValueCommand { Kind: SbCommandKind.Rotate });
    }

    [Fact]
    public void ConstantFlipH_MirrorsOffsetAndSetsFlag()
    {
        List<SbCommand> group = [new SbFlagCommand { Kind = SbCommandKind.FlipH, StartMs = 0, EndMs = 0 }]; // permanent
        var baker = new GroupTransformBaker(group, pivotX: 320, pivotY: 240, fps: 30);

        // offset tile 100 units right of the pivot must mirror to 100 units LEFT of it.
        var (x, y, commands) = baker.Bake(baseCenterX: 420, baseCenterY: 240, baseScale: 1, localStartMs: 1000, localEndMs: 2000, storyboardTimeOffsetMs: 0);

        Assert.Equal(220, x, precision: 3);
        Assert.Equal(240, y, precision: 3);
        Assert.Contains(commands, c => c is SbFlagCommand { Kind: SbCommandKind.FlipH });
    }

    [Fact]
    public void RejectsLoop() =>
        Assert.Throws<NotSupportedException>(() => new GroupTransformBaker(
            [new SbLoop { StartMs = 0, EndMs = 0, Count = 2, Children = [] }], 0, 0, 30));
}
