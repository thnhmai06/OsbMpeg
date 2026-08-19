using OsbMpeg.Compiler.Analysis;
using Xunit;

namespace OsbMpeg.Compiler.Tests;

public class QuadtreeMergerTests
{
    private const int TileSize = 64;
    private const double FrameDurationMs = 1000.0 / 30; // 33.333...

    // 2x2 grid of base tiles -> exactly one mergeable 2x2 block at level 1.
    private static TileGrid Grid()
    {
        return new TileGrid(TileSize * 2, TileSize * 2, TileSize);
    }

    private static TileRun Tile(int col, int row, double startMs, double endMs)
    {
        return new TileRun(col, row, col * TileSize, row * TileSize, TileSize, TileSize, startMs, endMs,
            new byte[TileSize * TileSize * 3]);
    }

    [Fact]
    public void MultiFrameSyncedBlock_StillMerges()
    {
        // A real "became static together" case: all 4 tiles share a 3-frame span.
        List<TileRun> batch =
        [
            Tile(0, 0, 0, 100), Tile(1, 0, 0, 100), Tile(0, 1, 0, 100), Tile(1, 1, 0, 100)
        ];

        var result = QuadtreeMerger.Merge(batch, Grid(), maxAssetPixels: 1_000_000, FrameDurationMs);

        var merged = Assert.Single(result);
        Assert.Equal(TileSize * 2, merged.Width);
        Assert.Equal(TileSize * 2, merged.Height);
    }

    [Fact]
    public void SingleFrameSyncedBlock_DoesNotMerge()
    {
        // Degenerate case: 4 independently-thrashing tiles that happen to close on the same
        // single frame, with nothing in common content-wise. Must stay separate so each can
        // become its own animation candidate.
        List<TileRun> batch =
        [
            Tile(0, 0, 0, FrameDurationMs), Tile(1, 0, 0, FrameDurationMs), Tile(0, 1, 0, FrameDurationMs),
            Tile(1, 1, 0, FrameDurationMs)
        ];

        var result = QuadtreeMerger.Merge(batch, Grid(), maxAssetPixels: 1_000_000, FrameDurationMs);

        Assert.Equal(4, result.Count);
        Assert.All(result, r => Assert.Equal(TileSize, r.Width));
    }

    [Fact]
    public void MismatchedDuration_DoesNotMerge()
    {
        List<TileRun> batch =
        [
            Tile(0, 0, 0, 100), Tile(1, 0, 0, 100), Tile(0, 1, 0, 100),
            Tile(1, 1, 0, 150) // one tile's run closes at a different time -> no shared block
        ];

        var result = QuadtreeMerger.Merge(batch, Grid(), maxAssetPixels: 1_000_000, FrameDurationMs);

        Assert.Equal(4, result.Count);
        Assert.All(result, r => Assert.Equal(TileSize, r.Width));
    }
}
