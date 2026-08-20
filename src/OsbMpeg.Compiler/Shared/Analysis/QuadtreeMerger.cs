using OsbMpeg.Parsers;

namespace OsbMpeg.Compiler.Shared.Analysis;

/// <summary>
///     Merges a batch of same-frame closed TileRuns (everything TileRunTracker.Advance
///     or .Flush returned for one moment) into larger runs wherever a power-of-two-aligned block
///     of tiles all became static together and stayed static together for the exact same interval.
///     This is a conservative, safe merge criterion by construction: a merge is just concatenating
///     pixel snapshots we already have in hand side by side into one bigger image, so it can never
///     introduce a wrong pixel — it only misses an opportunity when timing doesn't line up exactly
///     (routine on noisy/high-motion content, harmless, just falls back to per-tile granularity).
///     Runs directly out of TileRunTracker at a single canvas position (i.e. what we get called
///     with) never span more than one frame's worth of same-instant closures, so this stays
///     bounded by tile count — no extra memory beyond the batch itself.
/// </summary>
public static class QuadtreeMerger
{
    // Same rounding-safety margin as AnimationDetector's isSingleFrame check, and for the same
    // reason: millisecond-quantized frame boundaries land 1ms apart depending on rounding
    // direction, so "is this run exactly one frame long" needs slack wider than that jitter.
    private const double SingleFrameToleranceMs = 1.0;

    public static List<TileRun> Merge(List<TileRun> batch, TileGrid grid, long maxAssetPixels,
        double frameDurationMs)
    {
        if (batch.Count < 4)
            return batch;

        var byPos = new Dictionary<(int Col, int Row), TileRun>(batch.Count);
        foreach (var run in batch)
            byPos[(run.Col, run.Row)] = run;

        var consumed = new HashSet<(int Col, int Row)>();
        var result = new List<TileRun>();
        var maxLevel = MaxMergeLevel(grid, maxAssetPixels);

        for (var level = maxLevel; level >= 1; level--)
        {
            var blockTiles = 1 << level;
            for (var row = 0; row + blockTiles <= grid.Rows; row += blockTiles)
            for (var col = 0; col + blockTiles <= grid.Cols; col += blockTiles)
                if (TryMergeBlock(byPos, consumed, grid, col, row, blockTiles, frameDurationMs, out var merged))
                    result.Add(merged);
        }
        result.AddRange(batch.Where(run => !consumed.Contains((run.Col, run.Row))));

        return result;
    }

    private static bool TryMergeBlock(
        Dictionary<(int Col, int Row), TileRun> byPos, HashSet<(int Col, int Row)> consumed,
        TileGrid grid, int col, int row, int blockTiles, double frameDurationMs, out TileRun merged)
    {
        merged = null!;
        TileRun? first = null;

        for (var dr = 0; dr < blockTiles; dr++)
        for (var dc = 0; dc < blockTiles; dc++)
        {
            var pos = (col + dc, row + dr);
            if (consumed.Contains(pos) || !grid.IsFullTile(pos.Item1, pos.Item2) ||
                !byPos.TryGetValue(pos, out var run))
                return false;

            first ??= run;
            if (!run.StartMs.IsEqual(first.StartMs) || !run.EndMs.IsEqual(first.EndMs))
                return false;
        }

        // All sub-tiles closing at the exact same instant is necessary but not sufficient
        // evidence of genuinely-shared static content: on high-motion regions, tiles that
        // individually thrash every frame also close in lockstep purely from being equally
        // volatile, with nothing in common. A block whose shared duration is itself only one
        // frame long is that degenerate case -- a real "became static together" merge spans
        // multiple frames by definition. Merging it anyway is strictly worse than leaving the
        // sub-tiles separate: AnimationDetector excludes merged blocks from animation
        // consideration by construction, so this would forfeit each sub-tile's own shot at
        // becoming a cheap SbAnimation candidate for zero benefit.
        if ((first!.EndMs - first.StartMs).IsEqual(frameDurationMs, SingleFrameToleranceMs))
            return false;

        var tileSize = grid.TileSize;
        var blockSize = blockTiles * tileSize;
        var combined = new byte[blockSize * blockSize * 3];

        for (var dr = 0; dr < blockTiles; dr++)
        for (var dc = 0; dc < blockTiles; dc++)
        {
            var run = byPos[(col + dc, row + dr)];
            for (var yy = 0; yy < tileSize; yy++)
            {
                var srcOffset = yy * tileSize * 3;
                var dstOffset = ((dr * tileSize + yy) * blockSize + dc * tileSize) * 3;
                Buffer.BlockCopy(run.Rgb, srcOffset, combined, dstOffset, tileSize * 3);
            }

            consumed.Add((col + dc, row + dr));
        }

        merged = new TileRun(col, row, col * tileSize, row * tileSize, blockSize, blockSize, first!.StartMs,
            first.EndMs, combined);
        return true;
    }

    private static int MaxMergeLevel(TileGrid grid, long maxAssetPixels)
    {
        var levelByBudget = 0;
        while (true)
        {
            var size = (1L << (levelByBudget + 1)) * grid.TileSize;
            if (size * size > maxAssetPixels)
                break;
            levelByBudget++;
        }

        var smallerSide = Math.Max(1, Math.Min(grid.Cols, grid.Rows));
        var levelByGrid = (int)Math.Log2(smallerSide);
        return Math.Max(0, Math.Min(levelByBudget, levelByGrid));
    }
}