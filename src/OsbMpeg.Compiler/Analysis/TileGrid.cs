namespace OsbMpeg.Compiler.Analysis;

/// <summary>
///     Fixed grid over the canvas. Edge tiles are smaller than TileSize when the
///     canvas isn't an exact multiple — they are never padded, just cropped.
/// </summary>
public sealed class TileGrid(int canvasWidth, int canvasHeight, int tileSize)
{
    public int CanvasWidth { get; } = canvasWidth;
    public int CanvasHeight { get; } = canvasHeight;
    public int TileSize { get; } = tileSize;
    public int Cols { get; } = (canvasWidth + tileSize - 1) / tileSize;
    public int Rows { get; } = (canvasHeight + tileSize - 1) / tileSize;
    public int TileCount => Cols * Rows;

    /// <summary>
    ///     False for the last row/column when the canvas isn't an exact multiple of
    ///     TileSize — those tiles are cropped and can't participate in a power-of-two quadtree
    ///     merge (ragged block dimensions), so the merger skips them and leaves them at base
    ///     granularity.
    /// </summary>
    public bool IsFullTile(int col, int row)
    {
        return col * TileSize + TileSize <= CanvasWidth && row * TileSize + TileSize <= CanvasHeight;
    }

    public (int X, int Y, int Width, int Height) TileBounds(int col, int row)
    {
        var x = col * TileSize;
        var y = row * TileSize;
        return (x, y, Math.Min(TileSize, CanvasWidth - x), Math.Min(TileSize, CanvasHeight - y));
    }

    /// <summary>
    ///     Copies one tile's pixels out of a full-frame packed Rgb24 buffer (row-major,
    ///     CanvasWidth*3 bytes/row) into dest (tight-packed, tile Width*Height*3 bytes).
    /// </summary>
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