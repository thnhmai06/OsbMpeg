using OsbMpeg.Ir;
using OsbMpeg.Ir.Passes;
using Xunit;

namespace OsbMpeg.Tests;

public class DropNoOpCommandsTests
{
    private static SbValueCommand Fade(double start, double end, float startVal, float endVal) =>
        new() { Kind = SbCommandKind.Fade, StartMs = start, EndMs = end, Start = startVal, End = endVal };

    [Fact]
    public void FlatCommandNestedInWiderFlatCommand_IsDropped()
    {
        var commands = new List<SbCommand> { Fade(0, 3000, 1, 1), Fade(1000, 2000, 1, 1) };

        var dropped = DropNoOpCommands.Drop(commands);

        var single = Assert.Single(dropped);
        Assert.Equal(0, single.StartMs);
        Assert.Equal(3000, single.EndMs);
    }

    [Fact]
    public void ExactDuplicateFlatCommand_KeepsFirstOccurrenceOnly()
    {
        var commands = new List<SbCommand> { Fade(0, 1000, 1, 1), Fade(0, 1000, 1, 1) };

        var dropped = DropNoOpCommands.Drop(commands);

        Assert.Single(dropped);
    }

    [Fact]
    public void DifferentHeldValue_DoesNotDrop()
    {
        var commands = new List<SbCommand> { Fade(0, 3000, 1, 1), Fade(1000, 2000, 0.5f, 0.5f) };

        var dropped = DropNoOpCommands.Drop(commands);

        Assert.Equal(2, dropped.Count);
    }

    [Fact]
    public void NotFullyCovered_DoesNotDrop()
    {
        var commands = new List<SbCommand> { Fade(0, 1500, 1, 1), Fade(1000, 2000, 1, 1) };

        var dropped = DropNoOpCommands.Drop(commands);

        Assert.Equal(2, dropped.Count);
    }

    [Fact]
    public void NonFlatCommand_NeverDropped()
    {
        var commands = new List<SbCommand> { Fade(0, 3000, 0, 1), Fade(1000, 2000, 0.5f, 0.5f) };

        var dropped = DropNoOpCommands.Drop(commands);

        Assert.Equal(2, dropped.Count);
    }

    [Fact]
    public void DifferentKind_DoesNotDrop()
    {
        var commands = new List<SbCommand>
        {
            Fade(0, 3000, 1, 1),
            new SbValueCommand { Kind = SbCommandKind.MoveX, StartMs = 1000, EndMs = 2000, Start = 1, End = 1 },
        };

        var dropped = DropNoOpCommands.Drop(commands);

        Assert.Equal(2, dropped.Count);
    }

    [Fact]
    public void SoleCommandOnObject_NeverDropped()
    {
        var commands = new List<SbCommand> { Fade(0, 1000, 1, 1) };

        var dropped = DropNoOpCommands.Drop(commands);

        Assert.Single(dropped);
    }

    [Fact]
    public void NonValueCommands_PassThroughUnchanged()
    {
        var colour = new SbColourCommand { StartMs = 0, EndMs = 1000, Start = SbColor.White, End = SbColor.White };
        var flag = new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = 0, EndMs = 1000 };
        var commands = new List<SbCommand> { colour, flag };

        var dropped = DropNoOpCommands.Drop(commands);

        Assert.Equal(2, dropped.Count);
        Assert.Contains(colour, dropped);
        Assert.Contains(flag, dropped);
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
            Commands = [Fade(0, 3000, 1, 1), Fade(1000, 2000, 1, 1)],
        };
        doc.Add(sprite);

        DropNoOpCommands.Apply(doc);

        Assert.Single(sprite.Commands);
    }
}
