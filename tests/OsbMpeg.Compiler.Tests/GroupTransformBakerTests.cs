using OsbMpeg.Compiler.Compilation;
using OsbMpeg.Parsers.Ir;
using Xunit;

namespace OsbMpeg.Compiler.Tests;

public class GroupTransformBakerTests
{
    private static List<SbCommand> Fade(double start, double end, float from, float to)
    {
        return [new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = start, EndMs = end, Start = from, End = to }];
    }

    [Fact]
    public void ConstantSpanAfterFade_EmitsOneStaticCommandSet_NoFadeOrColour()
    {
        var baker = new GroupTransformBaker(Fade(0, 2000, 0, 1), 320, 240, 30);

        // run [3000,4000] is entirely after the fade ends -> alpha is constantly 1 there,
        // and colour/additive were never touched at all -> cheap static path, no F/C/P lines.
        var (x, y, commands) = baker.Bake(320, 240, 1, 3000, 4000, 0);

        Assert.Equal(320, x);
        Assert.Equal(240, y);
        Assert.Equal(4, commands.Count); // MoveX, MoveY, VectorScaleX, VectorScaleY only
        Assert.All(commands, c => Assert.Equal(3000, c.StartMs));
        Assert.All(commands, c => Assert.Equal(4000, c.EndMs));
    }

    [Fact]
    public void SpanOverlappingFade_SamplesPerFrame_FirstSampleMatchesFadeStart()
    {
        var baker = new GroupTransformBaker(Fade(0, 2000, 0, 1), 320, 240, 30);

        var (_, _, commands) = baker.Bake(320, 240, 1, 0, 2000, 0);

        Assert.True(commands.Count > 4, "a span overlapping an active fade must not take the static 4-command path");
        var firstFade =
            Assert.IsType<SbValueCommand>(commands.First(c => c is SbValueCommand { Kind: SbCommandKind.Fade }));
        Assert.Equal(0f, firstFade.Start);
    }

    [Fact]
    public void OffsetTile_CentersCorrectly_UnderUniformScale()
    {
        // No commands beyond a no-op far-future fade -> pure geometry check.
        var baker = new GroupTransformBaker(Fade(0, 1, 1, 1), 320, 240, 30);

        // A tile whose base (un-transformed) center sits 100 storyboard units right of the
        // pivot, under baseScale=0.5: with no Move/Scale commands the group holds at its
        // declared pivot, so the tile's position is pivot + (baseCenter-pivot)*scale = 320+100*1=420
        // (Scale/VectorScale default to 1, independent of baseScale which only affects the
        // tile's own size, not the position formula's scale factor here).
        var (x, y, _) = baker.Bake(420, 240, 0.5, 5000, 6000, 0);

        Assert.Equal(420, x, 3);
        Assert.Equal(240, y, 3);
    }

    [Fact]
    public void AdditiveTrueFalseTrue_EmitsTwoRevertingSpans_NotPermanentOn()
    {
        List<SbCommand> group =
        [
            new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = 0, EndMs = 500 },
            new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = 1000, EndMs = 1500 }
        ];
        var baker = new GroupTransformBaker(group, 320, 240, 30);

        var (_, _, commands) = baker.Bake(320, 240, 1, 0, 2000, 0);

        var flags = commands.OfType<SbFlagCommand>().Where(f => f.Kind == SbCommandKind.Additive).ToList();
        Assert.Equal(2, flags.Count);
        Assert.All(flags,
            f => Assert.True(f.EndMs > f.StartMs, "each window must revert (StartMs==EndMs would mean permanent-on)"));
    }

    [Fact]
    public void ConstantQuarterTurn_RotatesOffsetIntoCorrectQuadrant()
    {
        // theta = pi/2 permanently (StartMs==EndMs). An offset tile 100 units right of the
        // pivot (baseCenterX=420 vs pivotX=320) must swing to 100 units BELOW the pivot,
        // matching Compositor's [cos -sin; sin cos] convention: rotX = offX*cos - offY*sin,
        // rotY = offX*sin + offY*cos; with offX=100, offY=0, theta=pi/2 -> (0, 100).
        List<SbCommand> group =
        [
            new SbValueCommand
            {
                Kind = SbCommandKind.Rotate, StartMs = 0, EndMs = 0, Start = (float)(Math.PI / 2),
                End = (float)(Math.PI / 2)
            }
        ];
        var baker = new GroupTransformBaker(group, 320, 240, 30);

        var (x, y, commands) = baker.Bake(420, 240, 1, 1000, 2000, 0);

        Assert.Equal(320, x, 3);
        Assert.Equal(340, y, 3); // pivotY(240) + rotY(100)
        Assert.Contains(commands, c => c is SbValueCommand { Kind: SbCommandKind.Rotate });
    }

    [Fact]
    public void ConstantFlipH_MirrorsOffsetAndSetsFlag()
    {
        List<SbCommand> group = [new SbFlagCommand { Kind = SbCommandKind.FlipH, StartMs = 0, EndMs = 0 }]; // permanent
        var baker = new GroupTransformBaker(group, 320, 240, 30);

        // offset tile 100 units right of the pivot must mirror to 100 units LEFT of it.
        var (x, y, commands) = baker.Bake(420, 240, 1, 1000, 2000, 0);

        Assert.Equal(220, x, 3);
        Assert.Equal(240, y, 3);
        Assert.Contains(commands, c => c is SbFlagCommand { Kind: SbCommandKind.FlipH });
    }

    [Fact]
    public void LoopedFade_BakesAsIfFlattened()
    {
        // Loop starting at 0, 2 iterations of a 500ms fade-in -> equivalent to two separate
        // fades at [0,500] and [500,1000]. Sampling a run that only covers the second
        // iteration should see the fade restart from 0, not continue from where the first
        // iteration left off.
        List<SbCommand> group =
        [
            new SbLoop
            {
                StartMs = 0, EndMs = 0, Count = 2,
                Children =
                [
                    new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 0, EndMs = 500, Start = 0, End = 1 }
                ]
            }
        ];
        var baker = new GroupTransformBaker(group, 320, 240, 30);

        var (_, _, commands) = baker.Bake(320, 240, 1, 500, 1000, 0);

        var fades = commands.OfType<SbValueCommand>().Where(c => c.Kind == SbCommandKind.Fade).OrderBy(c => c.StartMs)
            .ToList();
        Assert.NotEmpty(fades);
        Assert.Equal(0f, fades[0].Start); // second iteration restarts the fade at 0, per the loop shape
    }

    [Fact]
    public void TriggerWithFadeChild_CopiedVerbatimOntoEveryTile()
    {
        List<SbCommand> group =
        [
            new SbTrigger
            {
                Name = "HitSoundClap", StartMs = 0, EndMs = 1000, Group = 0,
                Children =
                [
                    new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 0, EndMs = 500, Start = 0, End = 1 }
                ]
            }
        ];
        var baker = new GroupTransformBaker(group, 320, 240, 30);

        // Baked from two different tiles (different base centers) — the trigger must show up
        // identically on both, since it's position-independent and not something CommandEvaluator
        // ever samples (its fire time is unknown at compile time).
        var (_, _, commandsA) = baker.Bake(320, 240, 1, 0, 2000, 0);
        var (_, _, commandsB) = baker.Bake(420, 300, 1, 0, 2000, 0);

        var triggerA = Assert.Single(commandsA.OfType<SbTrigger>());
        var triggerB = Assert.Single(commandsB.OfType<SbTrigger>());
        Assert.Same(triggerA, triggerB); // same shared instance, not a per-tile copy — it's immutable data
        Assert.Equal("HitSoundClap", triggerA.Name);
        Assert.Single(triggerA.Children);
    }

    [Fact]
    public void RejectsMoveInsideTrigger()
    {
        List<SbCommand> group =
        [
            new SbTrigger
            {
                Name = "Passing", StartMs = 0, EndMs = 1000,
                Children =
                [
                    new SbValueCommand { Kind = SbCommandKind.MoveX, StartMs = 0, EndMs = 500, Start = 0, End = 100 }
                ]
            }
        ];

        var ex = Assert.Throws<NotSupportedException>(() => new GroupTransformBaker(group, 320, 240, 30));
        Assert.Contains("Passing", ex.Message);
    }
}