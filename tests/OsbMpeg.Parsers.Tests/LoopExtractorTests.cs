using OsbMpeg.Parsers.Ir;
using OsbMpeg.Parsers.Ir.Passes;
using Xunit;

namespace OsbMpeg.Parsers.Tests;

public class LoopExtractorTests
{
    private static List<SbCommand> Repeat(int iterations, double cycleLength)
    {
        // Contiguous, back-to-back iterations: duration == cycleLength, matching how L's replay
        // cadence is derived (LoopFlattener: cycleLength = max EndMs of the body).
        List<SbCommand> commands = [];
        for (var i = 0; i < iterations; i++)
            commands.Add(new SbValueCommand
            {
                Kind = SbCommandKind.Fade, StartMs = i * cycleLength, EndMs = (i + 1) * cycleLength, Start = 0,
                End = 1
            });
        return commands;
    }

    [Fact]
    public void PassesNonRepeatingCommandsThrough()
    {
        List<SbCommand> commands =
            [new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 0, EndMs = 100, Start = 0, End = 1 }];
        var extracted = LoopExtractor.Extract(commands);
        var item = Assert.Single(extracted);
        Assert.Same(commands[0], item);
    }

    [Fact]
    public void ThreeIdenticalIterations_ExtractedIntoSingleLoop()
    {
        var commands = Repeat(3, 500);
        var extracted = LoopExtractor.Extract(commands);

        var loop = Assert.IsType<SbLoop>(Assert.Single(extracted));
        Assert.Equal(0, loop.StartMs);
        Assert.Equal(3, loop.Count);
        var child = Assert.IsType<SbValueCommand>(Assert.Single(loop.Children));
        Assert.Equal(0, child.StartMs);
        Assert.Equal(500, child.EndMs);
    }

    [Fact]
    public void TwoIterations_BelowMinimum_NotExtracted()
    {
        var commands = Repeat(2, 500);
        var extracted = LoopExtractor.Extract(commands);

        Assert.Equal(2, extracted.Count);
        Assert.All(extracted, c => Assert.IsType<SbValueCommand>(c));
    }

    [Fact]
    public void MismatchedCadence_NotExtracted()
    {
        // Same values, but the third repeat drifts off the 500ms cadence -> not a real loop.
        List<SbCommand> commands =
        [
            new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 0, EndMs = 500, Start = 0, End = 1 },
            new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 500, EndMs = 1000, Start = 0, End = 1 },
            new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 1200, EndMs = 1700, Start = 0, End = 1 }
        ];

        var extracted = LoopExtractor.Extract(commands);

        Assert.Equal(3, extracted.Count);
        Assert.All(extracted, c => Assert.IsType<SbValueCommand>(c));
    }

    [Fact]
    public void RoundTrips_ThroughLoopFlattener()
    {
        var original = Repeat(5, 250);
        var extracted = LoopExtractor.Extract(original);
        var flattened = LoopFlattener.Flatten(extracted);

        Assert.Equal(original.Count, flattened.Count);
        var origVals = original.Cast<SbValueCommand>().OrderBy(c => c.StartMs).ToList();
        var flatVals = flattened.Cast<SbValueCommand>().OrderBy(c => c.StartMs).ToList();
        for (var i = 0; i < origVals.Count; i++)
        {
            Assert.Equal(origVals[i].StartMs, flatVals[i].StartMs, 3);
            Assert.Equal(origVals[i].EndMs, flatVals[i].EndMs, 3);
            Assert.Equal(origVals[i].Start, flatVals[i].Start, 3);
            Assert.Equal(origVals[i].End, flatVals[i].End, 3);
        }
    }

    [Fact]
    public void DifferentKinds_ExtractedIndependently()
    {
        List<SbCommand> commands =
        [
            .. Repeat(3, 500), // Fade block: genuinely repeating -> extracts to a loop
            new SbValueCommand { Kind = SbCommandKind.MoveX, StartMs = 0, EndMs = 100, Start = 0, End = 10 },
            new SbValueCommand { Kind = SbCommandKind.MoveX, StartMs = 100, EndMs = 200, Start = 10, End = 20 },
            new SbValueCommand { Kind = SbCommandKind.MoveX, StartMs = 200, EndMs = 300, Start = 20, End = 30 }
        ];

        var extracted = LoopExtractor.Extract(commands);

        // Fade block collapses to 1 SbLoop; MoveX values ramp (0->10->20->30), not a repeat, so
        // its 3 commands pass through unchanged. 1 loop + 3 passthrough = 4.
        Assert.Equal(4, extracted.Count);
        Assert.IsType<SbLoop>(extracted[0]);
        Assert.All(extracted.Skip(1), c => Assert.IsType<SbValueCommand>(c));
    }
}
