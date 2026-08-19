namespace OsbMpeg.Ir.Passes;

/// <summary>Drops a flat (Start==End) value command when another command on the same object,
/// same Kind, already spans its [StartMs,EndMs] with the identical flat value — removing it
/// changes nothing rendered. Narrower than MergeAdjacentCommands: targets nested/duplicate
/// redundancy, not adjacency. Never empties an object's command list: a command is only
/// dropped when a DIFFERENT command remains covering its range (IsDrawable => HasCommands).</summary>
public static class DropNoOpCommands
{
    private const float ValueEpsilon = 1e-4f;

    public static void Apply(SbDocument doc)
    {
        foreach (var obj in doc.AllObjects)
            obj.Commands = Drop(obj.Commands);
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
        if (Math.Abs(c.Start - c.End) > ValueEpsilon) return false; // not flat, can't be a no-op

        for (var j = 0; j < commands.Count; j++)
        {
            if (j == index || commands[j] is not SbValueCommand other) continue;
            if (other.Kind != c.Kind) continue;
            if (Math.Abs(other.Start - other.End) > ValueEpsilon) continue; // covering command must also be flat
            if (Math.Abs(other.Start - c.Start) > ValueEpsilon) continue; // same held value
            if (other.StartMs > c.StartMs || other.EndMs < c.EndMs) continue; // must fully cover c's span
            if (other.StartMs == c.StartMs && other.EndMs == c.EndMs && j > index) continue; // exact duplicate: keep first occurrence

            return true;
        }
        return false;
    }
}
