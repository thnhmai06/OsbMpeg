using OsbMpeg.Ir;
using OsbMpeg.Ir.Passes;
using Xunit;

namespace OsbMpeg.Tests;

public class MergeAdjacentCommandsTests
{
    private static SbValueCommand Move(double start, double end, float startVal, float endVal, SbEasing easing = SbEasing.None) =>
        new() { Kind = SbCommandKind.MoveX, StartMs = start, EndMs = end, Start = startVal, End = endVal, Easing = easing };

    [Fact]
    public void CollinearAdjacentSegments_MergeIntoOne()
    {
        // Constant-velocity motion sampled into two frame-boundary segments: 0->100 over 0-1000,
        // continued 100->200 over 1000-2000 — same slope (0.1/ms) on both sides.
        var commands = new List<SbCommand> { Move(0, 1000, 0, 100), Move(1000, 2000, 100, 200) };

        var merged = MergeAdjacentCommands.Merge(commands);

        var single = Assert.Single(merged);
        var v = Assert.IsType<SbValueCommand>(single);
        Assert.Equal(0, v.StartMs);
        Assert.Equal(2000, v.EndMs);
        Assert.Equal(0, v.Start);
        Assert.Equal(200, v.End);
    }

    [Fact]
    public void KinkInSlope_DoesNotMerge()
    {
        // 0->100 over 0-1000 (slope 0.1), then 100->100 over 1000-2000 (slope 0) — direction
        // changes at the join, so collapsing to one linear command would change the curve.
        var commands = new List<SbCommand> { Move(0, 1000, 0, 100), Move(1000, 2000, 100, 100) };

        var merged = MergeAdjacentCommands.Merge(commands);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void ValueDiscontinuity_DoesNotMerge()
    {
        var commands = new List<SbCommand> { Move(0, 1000, 0, 100), Move(1000, 2000, 150, 250) };

        var merged = MergeAdjacentCommands.Merge(commands);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void TimeGap_DoesNotMerge()
    {
        var commands = new List<SbCommand> { Move(0, 1000, 0, 100), Move(1500, 2500, 100, 200) };

        var merged = MergeAdjacentCommands.Merge(commands);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void DifferentKind_DoesNotMerge()
    {
        var commands = new List<SbCommand>
        {
            Move(0, 1000, 0, 100),
            new SbValueCommand { Kind = SbCommandKind.MoveY, StartMs = 1000, EndMs = 2000, Start = 100, End = 200 },
        };

        var merged = MergeAdjacentCommands.Merge(commands);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void DifferentEasing_DoesNotMerge()
    {
        var commands = new List<SbCommand> { Move(0, 1000, 0, 100, SbEasing.None), Move(1000, 2000, 100, 200, SbEasing.In) };

        var merged = MergeAdjacentCommands.Merge(commands);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void FlatRunsAtSameValue_Merge()
    {
        var commands = new List<SbCommand> { Move(0, 1000, 50, 50), Move(1000, 2000, 50, 50), Move(2000, 3000, 50, 50) };

        var merged = MergeAdjacentCommands.Merge(commands);

        var single = Assert.Single(merged);
        var v = Assert.IsType<SbValueCommand>(single);
        Assert.Equal(0, v.StartMs);
        Assert.Equal(3000, v.EndMs);
    }

    [Fact]
    public void ThreeCollinearSegments_MergeIntoOne()
    {
        var commands = new List<SbCommand> { Move(0, 500, 0, 50), Move(500, 1000, 50, 100), Move(1000, 1500, 100, 150) };

        var merged = MergeAdjacentCommands.Merge(commands);

        var single = Assert.Single(merged);
        var v = Assert.IsType<SbValueCommand>(single);
        Assert.Equal(0, v.StartMs);
        Assert.Equal(1500, v.EndMs);
        Assert.Equal(0, v.Start);
        Assert.Equal(150, v.End);
    }

    [Fact]
    public void NonValueCommands_PassThroughUnchanged()
    {
        var colour = new SbColourCommand { StartMs = 0, EndMs = 1000, Start = SbColor.White, End = SbColor.White };
        var flag = new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = 0, EndMs = 1000 };
        var commands = new List<SbCommand> { colour, flag };

        var merged = MergeAdjacentCommands.Merge(commands);

        Assert.Equal(2, merged.Count);
        Assert.Contains(colour, merged);
        Assert.Contains(flag, merged);
    }

    [Fact]
    public void ApplyOnDocument_MutatesEachObjectsCommandsInPlace()
    {
        var doc = new SbDocument();
        var sprite = new SbSprite
        {
            Layer = SbLayer.Background,
            Origin = SbOrigin.Centre,
            X = 0,
            Y = 0,
            Asset = new AssetId("a.png"),
            Commands = [Move(0, 1000, 0, 100), Move(1000, 2000, 100, 200)],
        };
        doc.Add(sprite);

        MergeAdjacentCommands.Apply(doc);

        Assert.Single(sprite.Commands);
    }
}
