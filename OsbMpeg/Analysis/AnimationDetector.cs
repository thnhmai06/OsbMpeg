namespace OsbMpeg.Analysis;

/// <summary>An accumulated sequence of same-position, exactly-one-frame-long runs, ready to
/// become one SbAnimation instead of N individual sprites+commands.</summary>
public sealed record AnimationCandidate(int PixelX, int PixelY, int Width, int Height, double StartMs, double EndMs, double FrameDelayMs, List<byte[]> Frames);

/// <summary>Upgrades a run of consecutive single-frame tile runs into one SbAnimation
/// candidate when a base tile keeps changing every single frame — the case where cross-tile
/// content-hash dedupe (the Sprite-per-run default) can't help anyway, so trading it away for
/// fewer sprites/commands is a clean win. See the design notes: Animation forfeits dedupe, so
/// it must only be used where dedupe wasn't going to fire regardless.
///
/// Only base tiles are candidates (Width/Height &lt;= tileSize) — a merged block from
/// QuadtreeMerger is, by construction, the opposite of thrashing and goes straight through
/// as an ordinary sprite.
///
/// A position that thrashes for the whole video would otherwise accumulate one pixel
/// snapshot per frame forever (unbounded memory on long footage). maxAccumulatedFrames caps
/// that: once a position hits the cap it's force-flushed as one Animation and accumulation
/// restarts, same as a real run boundary would do.</summary>
public sealed class AnimationDetector(double fps, int minAnimationFrames = 4, int maxAccumulatedFrames = 300)
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
            var isSingleFrame = Math.Abs(run.EndMs - run.StartMs - _frameDurationMs) < 0.5;

            if (isSingleFrame)
            {
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

        if (runs.Count >= minAnimationFrames)
        {
            var first = runs[0];
            animations.Add(new AnimationCandidate(
                first.PixelX, first.PixelY, first.Width, first.Height,
                first.StartMs, runs[^1].EndMs, _frameDurationMs,
                runs.ConvertAll(r => r.Rgb)));
        }
        else
        {
            sprites.AddRange(runs); // too short to be worth Animation's per-frame-file overhead
        }
    }
}
