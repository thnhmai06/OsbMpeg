using OsbMpeg.Compiler.Media;

namespace OsbMpeg.Compiler.Analysis;

/// <summary>
///     One closed run of a tile (or, after QuadtreeMerger, a merged block of tiles):
///     content stayed the same (by quantized-hash equality) from StartMs to EndMs. Rgb is the
///     tight-packed Rgb24 snapshot taken on the frame the run started — later frames in the run
///     are assumed identical, which is exactly what the hash match asserts. By default (see
///     TileRunTracker's canonicalSnapshot) these are the *quantized* bytes that were hashed, not
///     the raw pixels — so two runs that hash equal also produce byte-identical Rgb, which is what
///     lets AssetStore's content-hash dedupe actually collapse them into one asset. Col/Row
///     identify the top-left base tile's grid position (used for spatial grouping in the merger);
///     PixelX/PixelY/Width/Height are the actual canvas-pixel footprint, which for a merged run
///     spans multiple base tiles.
/// </summary>
public sealed record TileRun(
    int Col,
    int Row,
    int PixelX,
    int PixelY,
    int Width,
    int Height,
    double StartMs,
    double EndMs,
    byte[] Rgb);

/// <summary>
///     Per-tile state machine driving the conditional-replenishment backbone: as long
///     as a tile's content hash doesn't change, its current run just extends; a hash change closes
///     the run and starts a new one. This is the whole compression mechanism — most tiles in most
///     frames never close a run at all.
///     Global/region motion compensation (the original plan's optimizer phases 12-15) was
///     evaluated against this design and found structurally unviable, not just untuned — do not
///     re-attempt without solving both of these:
///     1. .osb has no predictive/residual coding — every Sprite is a full self-contained texture.
///     A motion vector used only to *predict* the fixed-grid tile at (x,y) from frame t-1
///     buys nothing: the asset bytes still come from ExtractTile at the fixed (x,y) crop of
///     frame t, which changes every frame under a pan regardless of what the vector says.
///     2. Making assets byte-identical across frames requires shifting the *sampling* grid to
///     track physical scene content — but then the *output* (on-screen) grid must shift by the
///     same amount to reconstruct correctly, which opens a coverage gap on one edge and an
///     overhang on the other. Closing that needs variable-size boundary tiles in TileGrid
///     (today only the trailing row/col is ragged, from a fixed TileSize not dividing the
///     canvas evenly) — a change to the one component every regression check in this codebase
///     anchors on. And even with that solved, real motion is sub-tile-per-frame, so holding a
///     run open across a pan means either per-frame M keyframes on every tile sprite (the exact
///     command bloat this backbone exists to avoid) or a grid-wide forced run cut every time
///     accumulated drift crosses one pixel — worse than today's no-compensation baseline at any
///     real pan speed. TrajectoryFitter/RegionTracker/OcclusionAnalyzer inherit this same
///     residual-coding gap at region granularity, not just global.
///     A follow-up worth recording precisely because it does NOT die at the format level like the
///     above: signed residual coding via a second Sprite normal-blended (no P command needed) on
///     top of a base, with the residual's sign/magnitude baked as per-pixel RGBA into its own PNG.
///     Confirmed representable straight from osu!framework's fragment shader
///     (Resources/Shaders/sh_Masking.h, getRoundedColor: `return v_Colour * texel;` — texel is the
///     raw PNG sample, so its per-pixel alpha genuinely participates in the blend, not just the
///     uniform Drawable.Alpha from an F command). What actually gates it: Normal blend is
///     `lerp(prediction, overlay, alpha)` per channel — reproducing an arbitrary target exactly
///     needs a per-pixel overlay colour that amounts to re-deriving the target image itself (zero
///     dedupe win over today's baseline), so any dedupe payoff requires degenerating the overlay
///     colour to a small sign palette (e.g. black/white) — and one alpha is then shared across
///     R/G/B, correct only where all three channels' deltas agree in sign at that pixel. Undecided
///     and worth measuring before building: on real (non-flattering) footage, what fraction of
///     pixels have sign(ΔR)=sign(ΔG)=sign(ΔB), and do the resulting (sign-map, alpha-map) tiles
///     actually dedupe better than raw tiles. Also priced in already: the base sprite can't close
///     its run while a residual depends on it, so this doubles asset count per covered tile —
///     hard constraint ⑤ (Ranking Criteria pixel/asset budget), not a soft cost.
/// </summary>
public sealed class TileRunTracker
{
    private readonly byte[] _canonicalScratch;
    private readonly bool _canonicalSnapshot;
    private readonly ulong?[] _currentHash;
    private readonly TileGrid _grid;
    private readonly int _hashQuantLevels;
    private readonly byte[]?[] _runSnapshot;
    private readonly double[] _runStartMs;
    private readonly byte[] _scratch;

    private readonly int _tolerance;

    /// <param name="canonicalSnapshot">
    ///     When true (default), the run's stored snapshot is the
    ///     quantized bytes that were hashed — so two tiles that hash equal also dedupe as the same
    ///     asset. When false, the snapshot is the raw pixel bytes (pre-canonical-dedupe behavior),
    ///     kept only for A/B benchmarking via --raw-snapshot.
    /// </param>
    /// <param name="tolerance">
    ///     0 disables (default — a hash change always cuts the run, as
    ///     before). &gt;0 is a mean-absolute-error budget (0-255 per channel): when the hash
    ///     changes, the run is only cut if the *raw* current tile actually differs from what's
    ///     already being displayed (the run's snapshot) by more than this. This confirms hash
    ///     mismatches are real content changes, not the hash proposal's own quantization cliff —
    ///     see ContentHasher's design note. Comparison is always against the run's original
    ///     snapshot, never the previous frame, so error can't accumulate across a long run.
    /// </param>
    public TileRunTracker(TileGrid grid, int hashQuantLevels, bool canonicalSnapshot = true, int tolerance = 0)
    {
        _grid = grid;
        _hashQuantLevels = hashQuantLevels;
        _canonicalSnapshot = canonicalSnapshot;
        _tolerance = tolerance;
        _currentHash = new ulong?[grid.TileCount];
        _runStartMs = new double[grid.TileCount];
        _runSnapshot = new byte[grid.TileCount][];
        _scratch = new byte[grid.TileSize * grid.TileSize * 3];
        _canonicalScratch = new byte[grid.TileSize * grid.TileSize * 3];
    }

    public List<TileRun> Advance(VideoFrame frame, double frameMs)
    {
        var closed = new List<TileRun>();

        for (var row = 0; row < _grid.Rows; row++)
        for (var col = 0; col < _grid.Cols; col++)
        {
            var index = row * _grid.Cols + col;
            var (x, y, w, h) = _grid.TileBounds(col, row);
            var tile = _scratch.AsSpan(0, w * h * 3);
            _grid.ExtractTile(frame.Rgb, col, row, tile);
            var canonical = _canonicalScratch.AsSpan(0, w * h * 3);
            var hash = ContentHasher.Hash(tile, _hashQuantLevels, canonical);

            if (_currentHash[index] == hash)
                continue;

            if (_tolerance > 0 && _currentHash[index] is not null
                               && WithinTolerance(tile, _runSnapshot[index]!, _tolerance))
                continue; // hash proposal drifted, but the actual pixels are still close enough to what's already shown — keep the run open

            if (_currentHash[index] is not null)
                closed.Add(new TileRun(col, row, x, y, w, h, _runStartMs[index], frameMs, _runSnapshot[index]!));

            _currentHash[index] = hash;
            _runStartMs[index] = frameMs;
            _runSnapshot[index] = (_canonicalSnapshot ? canonical : (ReadOnlySpan<byte>)tile).ToArray();
        }

        return closed;
    }

    /// <summary>
    ///     True when mean absolute per-byte error between the raw tile and the run's
    ///     stored snapshot is within tolerance. Early-exits once the running sum can no longer fit
    ///     the budget, so a tile that's obviously different (the common case on a real cut) doesn't
    ///     pay for a full scan.
    /// </summary>
    private static bool WithinTolerance(ReadOnlySpan<byte> tile, byte[] snapshot, int tolerance)
    {
        var budget = (long)tolerance * tile.Length;
        long sum = 0;
        for (var i = 0; i < tile.Length; i++)
        {
            sum += Math.Abs(tile[i] - snapshot[i]);
            if (sum > budget)
                return false;
        }

        return true;
    }

    /// <summary>Closes every still-open run at the end of the video.</summary>
    public List<TileRun> Flush(double endMs)
    {
        var closed = new List<TileRun>();
        for (var row = 0; row < _grid.Rows; row++)
        for (var col = 0; col < _grid.Cols; col++)
        {
            var index = row * _grid.Cols + col;
            if (_currentHash[index] is null)
                continue;

            var (x, y, w, h) = _grid.TileBounds(col, row);
            closed.Add(new TileRun(col, row, x, y, w, h, _runStartMs[index], endMs, _runSnapshot[index]!));
        }

        return closed;
    }
}