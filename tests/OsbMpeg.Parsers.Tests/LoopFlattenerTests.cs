using OsbMpeg.Parsers.Ir;
using Xunit;

namespace OsbMpeg.Parsers.Tests;

public class LoopFlattenerTests
{
    [Fact]
    public void PassesNonLoopCommandsThrough()
    {
        List<SbCommand> commands =
            [new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 0, EndMs = 100, Start = 0, End = 1 }];
        var flat = LoopFlattener.Flatten(commands);
        var item = Assert.Single(flat);
        Assert.Same(commands[0], item);
    }

    [Fact]
    public void ReplicatesLoopBody_BackToBack_ZeroBasedShiftedByLoopStart()
    {
        // Matches the osu-wiki example shape: a fade-in spanning the first half of each cycle.
        // loopStart=60000, cycle length = max child EndMs = 500, 3 iterations.
        List<SbCommand> commands =
        [
            new SbLoop
            {
                StartMs = 60000,
                EndMs = 60000,
                Count = 3,
                Children =
                [
                    new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 0, EndMs = 500, Start = 0, End = 1 }
                ]
            }
        ];

        var flat = LoopFlattener.Flatten(commands);

        Assert.Equal(3, flat.Count);
        var fades = flat.Cast<SbValueCommand>().OrderBy(f => f.StartMs).ToList();
        Assert.Equal([60000, 60500, 61000], fades.Select(f => f.StartMs));
        Assert.Equal([60500, 61000, 61500], fades.Select(f => f.EndMs));
    }

    [Fact]
    public void CountZeroOrOne_RunsExactlyOnce()
    {
        List<SbCommand> commands =
        [
            new SbLoop
            {
                StartMs = 1000, EndMs = 1000, Count = 0,
                Children =
                [
                    new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 0, EndMs = 100, Start = 0, End = 1 }
                ]
            }
        ];

        Assert.Single(LoopFlattener.Flatten(commands));
    }

    [Fact]
    public void NestedLoop_FlattensInsideOutSoOuterCycleLengthAccountsForFullInnerDuration()
    {
        // Inner loop: 2 iterations of a 200ms fade -> inner expands to 400ms total.
        // Outer cycle length must be 400 (the inner's true expanded span), not the inner
        // SbLoop's own (unused) EndMs field.
        SbLoop inner = new()
        {
            StartMs = 0,
            EndMs = 0,
            Count = 2,
            Children = [new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 0, EndMs = 200, Start = 0, End = 1 }]
        };
        List<SbCommand> commands =
        [
            new SbLoop { StartMs = 5000, EndMs = 5000, Count = 2, Children = [inner] }
        ];

        var flat = LoopFlattener.Flatten(commands);

        Assert.Equal(4, flat.Count); // 2 outer iterations x 2 inner iterations
        var starts = flat.Cast<SbValueCommand>().Select(f => f.StartMs).OrderBy(t => t).ToList();
        Assert.Equal([5000, 5200, 5400, 5600], starts);
    }
}