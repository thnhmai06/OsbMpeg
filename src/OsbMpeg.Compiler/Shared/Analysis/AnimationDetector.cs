using System.IO.Hashing;
using OsbMpeg.Parsers;

namespace OsbMpeg.Compiler.Shared.Analysis;

/// <summary>
///     An accumulated sequence of same-position, exactly-one-frame-long runs, ready to
///     become one SbAnimation instead of N individual sprites+commands.
/// </summary>
public sealed record AnimationCandidate(
    int PixelX,
    int PixelY,
    int Width,
    int Height,
    double StartMs,
    double EndMs,
    double FrameDelayMs,
    List<byte[]> Frames);

/// <summary>
///     Upgrades a run of consecutive single-frame tile runs into one SbAnimation
///     candidate when a base tile keeps changing every single frame — the case where cross-tile
///     content-hash dedupe (the Sprite-per-run default) can't help anyway, so trading it away for
///     fewer sprites/commands is a clean win. See the design notes: Animation forfeits dedupe, so
///     it must only be used where dedupe wasn't going to fire regardless.
///     Only base tiles are candidates (Width/Height &lt;= tileSize) — a merged block from
///     QuadtreeMerger is, by construction, the opposite of thrashing and goes straight through
///     as an ordinary sprite.
///     A position that thrashes for the whole video would otherwise accumulate one pixel
///     snapshot per frame forever (unbounded memory on long footage). maxAccumulatedFrames caps
///     that: once a position hits the cap it's force-flushed as one Animation and accumulation
///     restarts, same as a real run boundary would do.
///     Being 1-frame-long (thrashing) is not the same as "dedupe can't help" — a tile flickering
///     A,B,A,B,A,B is thrashing but only has 2 distinct contents, and Sprite+dedupe collapses that
///     to 2 assets vs Animation's N per-frame files. minUniqueness gates on that directly: only
///     promote when most frames in the run really are distinct content (a genuinely no-dedupe
///     case), otherwise fall back to sprites and let AssetStore's dedupe do its job. Requires
///     canonical (quantized) snapshots — see TileRunTracker — since uniqueness is measured by
///     byte-equality and raw pixel noise would make every frame look "distinct" regardless.
///     A position mid-thrash can still miss a frame here without TileRunTracker ever closing a
///     multi-frame run for it: QuadtreeMerger runs on each frame's whole closed-run batch *before*
///     this class sees it, and if this position's closure happens to share the exact same
///     StartMs/EndMs as 3+ neighbors that one frame, it gets folded into a merged block and routed
///     straight to sprites — silently absent from this frame's batch for this position. Left
///     unchecked, the next single-frame run for the same position would just get appended to the
///     same pending list, producing an AnimationCandidate whose FrameCount undercounts its own
///     (EndMs-StartMs) span; LoopOnce then freezes on the last real frame for the missing stretch
///     — a static patch sitting on top of what should still be changing content. Process() checks
///     for exactly this (a new run that doesn't start where the last accumulated one ended) and
///     flushes-and-restarts instead of silently bridging the gap.
/// </summary>
public sealed class AnimationDetector(
    double fps,
    int minAnimationFrames = 4,
    int maxAccumulatedFrames = 300,
    double minUniqueness = 0.8)
{
    private readonly double _frameDurationMs = 1000.0 / fps;
    private readonly Dictionary<(int Col, int Row), List<TileRun>> _pending = new();

    public (List<TileRun> Sprites, List<AnimationCandidate> Animations) Process(List<TileRun> batch, int tileSize)
    {
        var sprites = new List<TileRun>();
        var animations = new List<AnimationCandidate>();

        foreach (var run in batch)
        {
            if (run.Width > tileSize || run.Height > tileSize)
            {
                sprites.Add(run); // merged block — never a thrash candidate
                continue;
            }

            var pos = (run.Col, run.Row);
            // Millisecond-quantized frame boundaries alternate between floor and ceil of the true
            // (fractional) frame duration -- e.g. at 30fps (33.333ms/frame) real single-frame runs
            // measure 33ms two-thirds of the time and 34ms the rest. A tolerance under ~1ms only
            // ever matches one side of that split, misclassifying the other as a "stable" run and
            // fragmenting what should be one continuous accumulation into many too-short pieces
            // that never reach minAnimationFrames. 1ms safely covers both sides of the rounding
            // split while staying far short of a genuine 2-frame run (~2x this duration away).
            var isSingleFrame = (run.EndMs - run.StartMs).IsEqual(_frameDurationMs, 1.0);

            if (isSingleFrame)
            {
                if (_pending.TryGetValue(pos, out var existing) && existing.Count > 0 &&
                    !existing[^1].EndMs.IsEqual(run.StartMs))
                    FlushPosition(pos, sprites, animations); // gap since the last accumulated frame — don't bridge it

                if (!_pending.TryGetValue(pos, out var list))
                    _pending[pos] = list = [];
                list.Add(run);
                if (list.Count >= maxAccumulatedFrames)
                    FlushPosition(pos, sprites, animations);
                continue;
            }

            // this position just became stable for more than one frame: the thrash (if any) ended
            FlushPosition(pos, sprites, animations);
            sprites.Add(run);
        }

        return (sprites, animations);
    }

    /// <summary>Call once at end of video to flush every position still mid-thrash.</summary>
    public (List<TileRun> Sprites, List<AnimationCandidate> Animations) FlushAll()
    {
        var sprites = new List<TileRun>();
        var animations = new List<AnimationCandidate>();
        foreach (var pos in _pending.Keys.ToList())
            FlushPosition(pos, sprites, animations);
        return (sprites, animations);
    }

    private void FlushPosition((int Col, int Row) pos, List<TileRun> sprites, List<AnimationCandidate> animations)
    {
        if (!_pending.Remove(pos, out var runs) || runs.Count == 0)
            return;

        if (runs.Count >= minAnimationFrames && Uniqueness(runs) >= minUniqueness)
        {
            var first = runs[0];
            animations.Add(new AnimationCandidate(
                first.PixelX, first.PixelY, first.Width, first.Height,
                first.StartMs, runs[^1].EndMs, _frameDurationMs,
                runs.ConvertAll(r => r.Rgb)));
        }
        else
        {
            sprites.AddRange(runs); // too short, or dedupe would win anyway — let GetOrAdd handle it
        }
    }

    /// <summary>
    ///     Fraction of runs whose canonical bytes are distinct from each other. 1.0 means
    ///     every frame is genuinely unique content (Animation has no dedupe to lose); low values
    ///     mean the run is really just a small set of contents repeating (Sprite+dedupe wins).
    /// </summary>
    private static double Uniqueness(List<TileRun> runs)
    {
        var distinct = new HashSet<UInt128>(runs.Count);
        foreach (var r in runs)
            distinct.Add(XxHash128.HashToUInt128(r.Rgb, 0));

        return (double)distinct.Count / runs.Count;
    }
}