namespace OsbMpeg.Parsers.Ir.Passes;

/// <summary>
///     Drops a flat (Start==End) value command when another command on the same object,
///     same Kind, already spans its [StartMs,EndMs] with the identical flat value — removing it
///     changes nothing rendered. Narrower than MergeAdjacentCommands: targets nested/duplicate
///     redundancy, not adjacency. Never empties an object's command list: a command is only
///     dropped when a DIFFERENT command remains covering its range (IsDrawable => HasCommands).
/// </summary>
public static class DropNoOpCommands
{
    private const float Epsilon = 1e-4f;

    public static void Apply(SbDocument doc)
    {
        foreach (var obj in doc.AllObjects)
        {
            var dropped = Drop(obj.Commands);
            obj.Commands.Clear();
            obj.Commands.AddRange(dropped);
        }
    }

    public static List<SbCommand> Drop(List<SbCommand> commands)
    {
        var result = new List<SbCommand>(commands.Count);
        for (var i = 0; i < commands.Count; i++)
        {
            if (commands[i] is SbValueCommand c && IsRedundant(c, i, commands))
                continue;
            result.Add(commands[i]);
        }

        return result;
    }

    private static bool IsRedundant(SbValueCommand c, int index, List<SbCommand> commands)
    {
        if (!c.Start.IsEqual(c.End, Epsilon)) return false; // not flat, can't be a no-op

        for (var j = 0; j < commands.Count; j++)
        {
            if (j == index || commands[j] is not SbValueCommand other) continue;
            if (other.Kind != c.Kind) continue;
            if (!other.Start.IsEqual(other.End, Epsilon)) continue; // covering command must also be flat
            if (!other.Start.IsEqual(c.Start, Epsilon)) continue; // same held value
            if (other.StartMs > c.StartMs || other.EndMs < c.EndMs) continue; // must fully cover c's span
            if (other.StartMs.IsEqual(c.StartMs) && other.EndMs.IsEqual(c.EndMs) && j > index)
                continue; // exact duplicate: keep first occurrence

            return true;
        }

        return false;
    }
}