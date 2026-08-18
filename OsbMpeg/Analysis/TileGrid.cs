namespace OsbMpeg.Analysis;

/// <summary>Fixed grid over the canvas. Edge tiles are smaller than TileSize when the
/// canvas isn't an exact multiple — they are never padded, just cropped.</summary>
public sealed class TileGrid
{
    public int TileSize { get; }
    public int CanvasWidth { get; }
    public int CanvasHeight { get; }
    public int Cols { get; }
    public int Rows { get; }
    public int TileCount => Cols * Rows;

    public TileGrid(int canvasWidth, int canvasHeight, int tileSize)
    {
        TileSize = tileSize;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Cols = (canvasWidth + tileSize - 1) / tileSize;
        Rows = (canvasHeight + tileSize - 1) / tileSize;
    }

    /// <summary>False for the last row/column when the canvas isn't an exact multiple of
    /// TileSize — those tiles are cropped and can't participate in a power-of-two quadtree
    /// merge (ragged block dimensions), so the merger skips them and leaves them at base
    /// granularity.</summary>
    public bool IsFullTile(int col, int row) => col * TileSize + TileSize <= CanvasWidth && row * TileSize + TileSize <= CanvasHeight;

    public (int X, int Y, int Width, int Height) TileBounds(int col, int row)
    {
        var x = col * TileSize;
        var y = row * TileSize;
        return (x, y, Math.Min(TileSize, CanvasWidth - x), Math.Min(TileSize, CanvasHeight - y));
    }

    /// <summary>Copies one tile's pixels out of a full-frame packed Rgb24 buffer (row-major,
    /// CanvasWidth*3 bytes/row) into dest (tight-packed, tile Width*Height*3 bytes).</summary>
    public void ExtractTile(ReadOnlySpan<byte> frameRgb, int col, int row, Span<byte> dest)
    {
        var (x, y, w, h) = TileBounds(col, row);
        for (var r = 0; r < h; r++)
        {
            var srcOffset = ((y + r) * CanvasWidth + x) * 3;
            var dstOffset = r * w * 3;
            frameRgb.Slice(srcOffset, w * 3).CopyTo(dest.Slice(dstOffset, w * 3));
        }
    }
}
