using OsbMpeg.Media;

namespace OsbMpeg.Analysis;

/// <summary>One closed run of a tile: content stayed the same (by quantized-hash equality)
/// from StartMs to EndMs. Rgb is the tight-packed Rgb24 snapshot taken on the frame the run
/// started — later frames in the run are assumed identical, which is exactly what the hash
/// match asserts.</summary>
public sealed record TileRun(int Col, int Row, double StartMs, double EndMs, byte[] Rgb, int Width, int Height);

/// <summary>Per-tile state machine driving the conditional-replenishment backbone: as long
/// as a tile's content hash doesn't change, its current run just extends; a hash change closes
/// the run and starts a new one. This is the whole compression mechanism — most tiles in most
/// frames never close a run at all.</summary>
public sealed class TileRunTracker
{
    private readonly TileGrid _grid;
    private readonly int _hashQuantLevels;
    private readonly ulong?[] _currentHash;
    private readonly double[] _runStartMs;
    private readonly byte[]?[] _runSnapshot;
    private readonly byte[] _scratch;

    public TileRunTracker(TileGrid grid, int hashQuantLevels)
    {
        _grid = grid;
        _hashQuantLevels = hashQuantLevels;
        _currentHash = new ulong?[grid.TileCount];
        _runStartMs = new double[grid.TileCount];
        _runSnapshot = new byte[grid.TileCount][];
        _scratch = new byte[grid.TileSize * grid.TileSize * 3];
    }

    public List<TileRun> Advance(VideoFrame frame, double frameMs)
    {
        var closed = new List<TileRun>();

        for (var row = 0; row < _grid.Rows; row++)
        {
            for (var col = 0; col < _grid.Cols; col++)
            {
                var index = row * _grid.Cols + col;
                var (_, _, w, h) = _grid.TileBounds(col, row);
                var tile = _scratch.AsSpan(0, w * h * 3);
                _grid.ExtractTile(frame.Rgb, col, row, tile);
                var hash = ContentHasher.Hash(tile, _hashQuantLevels);

                if (_currentHash[index] == hash)
                    continue;

                if (_currentHash[index] is not null)
                    closed.Add(new TileRun(col, row, _runStartMs[index], frameMs, _runSnapshot[index]!, w, h));

                _currentHash[index] = hash;
                _runStartMs[index] = frameMs;
                _runSnapshot[index] = tile.ToArray();
            }
        }

        return closed;
    }

    /// <summary>Closes every still-open run at the end of the video.</summary>
    public List<TileRun> Flush(double endMs)
    {
        var closed = new List<TileRun>();
        for (var row = 0; row < _grid.Rows; row++)
        {
            for (var col = 0; col < _grid.Cols; col++)
            {
                var index = row * _grid.Cols + col;
                if (_currentHash[index] is null)
                    continue;

                var (_, _, w, h) = _grid.TileBounds(col, row);
                closed.Add(new TileRun(col, row, _runStartMs[index], endMs, _runSnapshot[index]!, w, h));
            }
        }
        return closed;
    }
}
