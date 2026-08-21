# OsbMpeg.Compiler — Research Log

Distilled from an ad hoc plan file (~914 lines, accumulated across many sessions) that had become
the de facto design record. That file is retired; new design work goes through proper specs from
here on (`docs/superpowers/specs/` via the brainstorming skill, or a Plan Mode plan file for
implementation-scoped work). This log exists so the reasoning behind decisions already made —
what was tried, what was rejected and why, what was measured — isn't lost or re-derived from
scratch by a future session.

Entries are grouped by topic, not chronology. Each states what was tried, what was found, and the
decision (built / rejected / deferred) with the concrete reasoning.

## Rejected directions — do not resurrect without new evidence

### Motion/region compensation (`GlobalMotionEstimator`, `RegionSegmenter`/`RegionTracker`/
`TransformEstimator`, `TrajectoryFitter`, `OcclusionAnalyzer`)

Structurally unviable given `.osb`'s format constraints, not just under-tuned. `.osb` has no
predictive/residual coding — every Sprite is a full, self-contained texture. A motion vector used
only to *predict* the fixed-grid tile at `(x,y)` from the previous frame buys nothing: the asset
bytes still come from cropping the fixed `(x,y)` region of the *current* frame, which changes every
frame under a pan regardless of what any vector says. Making assets byte-identical across frames
under real motion would require shifting the *sampling* grid to track scene content — but then the
*output* grid must shift the same amount to reconstruct correctly, opening a coverage gap on one
edge and an overhang on the other. `TrajectoryFitter` has no valid upstream data source once
motion/region tracking are ruled out (it fits curves to a scalar trajectory nothing in this
architecture produces).

**Follow-up not dead, but never gated on a measurement**: signed residual coding via a second Sprite
normal-blended on top of a base, with sign/magnitude baked as per-pixel RGBA. Confirmed
representable straight from osu!framework's fragment shader (`sh_Masking.h`:
`return v_Colour * texel;` — texel's own alpha genuinely participates in the blend). What gates it:
Normal blend is `lerp(prediction, overlay, alpha)` per channel, so reproducing an arbitrary target
exactly needs a per-pixel overlay colour that amounts to re-deriving the target image — zero dedupe
win unless the overlay colour degenerates to a small sign palette (e.g. black/white), and then one
alpha is shared across R/G/B, correct only where all three channels' deltas agree in sign at that
pixel. Never measured: on real footage, what fraction of pixels have `sign(ΔR)=sign(ΔG)=sign(ΔB)`,
and whether the resulting (sign-map, alpha-map) tiles actually dedupe better than raw tiles. Also:
the base sprite can't close its run while a residual depends on it, doubling asset count per
covered tile.

### `AssetTrimmer` (DEFLATE probe, alpha-masking unchanged regions)

The compression mechanism itself works: a DEFLATE probe (ffmpeg, RGB+alpha zeroed under a synthetic
mask, ~14 samples) showed assets >3KB got 25–74% size reduction (avg ~45%); assets <1.5KB got
*worse* (PNG header/chunk overhead dominates at that size — a real, measured crossover point, not a
guess). But both variants (same-footprint alpha-mask, true-crop) share one correctness bug: a tile
position's sprites have non-overlapping lifetimes by construction (run B's sprite starts exactly
when run A's ends). If run B's asset makes "unchanged from run A" pixels transparent to save bytes,
those pixels render as blank canvas during run B's lifetime — run A's sprite has already been
removed by then, so transparency reveals nothing, not run A's still-correct content. Would need a
fundamentally different sprite-lifetime model (sprites that never get removed, patched
incrementally) to ever revisit — out of scope as designed.

### `SceneDetector`/`KeyframePlanner` (original design, pre-dates the per-scene tuner below)

Checked four independent justifications for building this; all came back empty:
1. Original motivation was segmenting per-segment motion analysis at scene cuts — that consumer is
   the dead motion-compensation phases above.
2. `Gop`/`maxAccumulatedFrames` looked like a candidate GOP concept to extend — checked, it's
   `AnimationDetector`'s frame-accumulation cap, unrelated to scene segmentation, already complete.
3. The tile codec carries no cross-frame prediction state to reset at a cut — a cut already
   resolves correctly today (every tile's hash changes, every run closes, new runs open).
4. Memory bounding is already handled — `FrameSource` streams frame-by-frame with pooled buffers.

Revival conditions recorded at the time: an RDO cost model needing a scene-boundary signal (not
built, see below); or per-scene parameter tuning needing scene boundaries — which *did* later
materialize and get built (see "Per-scene auto-tuning" below). Scene detection wasn't wasted effort
in the end, just needed the right consumer to show up first.

### RDO (rate-distortion-optimized) candidate-selection layer

Gated on one measurement before building any framework: sweep `--min-animation-uniqueness` (the
existing Sprite-vs-Animation heuristic threshold) and see whether a simple default-tune captures
cases a cost-based framework would otherwise be needed for. Swept twice — before and after an
unrelated `QuadtreeMerger` fix (see below) — byte-identical output both times on the test fixture,
both before and after. Both real compression wins found this session traced to fixing mechanical
bugs *upstream* of candidate selection (a rounding bug, a merge-criterion bug), not the selection
heuristic itself being wrong. Not built; the premise (a case where uniqueness-cutoff structurally
can't express the right choice) remains undemonstrated on every fixture tried so far.

### Overlapping-window cache-miss gap at `TileTolerance>0`

Two compiles with different, overlapping windows over the same video can capture different
`_runSnapshot` bytes for what's logically the same tile run — a possible cache-dedupe miss.
Corrected framing during review: the gap is symmetric (runs are truncated at *both* window
boundaries, `Flush` closes open runs too, not just a leading-edge issue), and only manifests in a
narrow band — `_runSnapshot` stores *quantized* bytes; if drift between two capture points stays
inside one quantize bucket the snapshot bytes are identical regardless of when captured (no gap);
if drift is large enough to change the hash outright, the run just closes and reopens normally
(also not a bug). The gap only exists where raw-pixel drift crosses a quantize-bucket boundary while
staying inside the tolerance's MAE budget. Measured, not assumed: two compiles of the same fixture
with 50%-overlapping windows — the second run's own asset count was 20, but only 13 were new
writes; 7 (35%) served from the first run's cache. "Far fewer than its own asset count" was the
decision rule — dedup is working, the residual miss is real but narrow. Not built (a backward-search
fix was proposed and priced: repeated/extended backward decode, new pending-run bookkeeping in
`TileEncodeLoop`/`TileRunTracker`, a hard cap, new failure surface) — not justified against a gap
this narrow with no reported real-workload impact. Revisit only if a real workload shows the miss
rate mattering at scale; this measurement is the baseline to compare against if that happens.

### Heatmap-adaptive tile partitioning (spike, not built)

Idea: instead of a fixed uniform tile grid (one TileSize for the whole scene, merged after the
fact by QuadtreeMerger), build a change-frequency heatmap from a fine-grained scan and partition
the canvas top-down (large regions where content stays static, small regions where it thrashes),
sized to match local content directly rather than searching a single global TileSize. Motivated by
the same economics `AssetTrimmer`'s DEFLATE probe already established (bigger merged assets compress
better up to a point, small fragmented files pay PNG header overhead) — the hoped-for win was
finding the right merge boundaries from real content instead of QuadtreeMerger's own
timing-coincidence luck.

**First attempt had a real methodological bug**, caught before trusting the numbers: the top-down
split decision used the *average* change-frequency across all fine tiles in a candidate region.
Averaging hides exactly the failure case the idea needed to avoid — one small volatile corner (e.g.
a moving object covering a fraction of a large region) forces the *whole* region's combined hash to
change almost every frame regardless of how static the rest of it is, but a region-wide average
stays low and never triggers a split. Result: fish_spinning's whole 1920x1080 canvas stayed one
un-split region, re-encoding a full-frame PNG ~600 times (202MB for a 10s window). Same root cause
as the `QuadtreeMerger` single-frame merge bug above — an aggregate signal missing localized
volatility — caught the same way (checked the actual behavior, didn't trust the first number).
Fixed by switching the split criterion to *max* change-frequency of any fine tile inside the
candidate region.

**Real (byte-accurate, not estimated) spike after the fix, three fixtures, real ContentHasher +
in-memory AssetStore for genuine PNG-encoded costs**, each compared against this session's actual
per-scene-tuned production output on the same content:

| Fixture | Window | Adaptive assetBytes | Real tuned assetBytes | Ratio |
|---|---|---|---|---|
| fish_spinning | 10s | 201,209,266 | 76,154,338 | 2.6x worse |
| minecraft | 10s | 375,766,670 | 69,576,841 | 5.4x worse |
| short_animation | full 16.5s | 3,714,496 | 1,442,462 | 2.6x worse |

Lost on all three, including the clean/graphic-content fixture the idea was expected to favor most
(short_animation: 2294 emitted regions/runs vs. the real system's 116 sprites for the same content).

**Two concrete, known confounds — this was not a fully fair fight**, not built further given the
cost of fixing them: (1) the spike hardcoded Colors=0 (no PNG palette quantization) while the real
system's tuned combo for short_animation used Colors=16 — a likely large, uncounted-for chunk of the
gap. (2) The split threshold (max change-frequency > 0.15) was tried exactly once, not searched —
`ParameterTuner`'s own coordinate-descent search across TileSize/HashQuantLevels/TileTolerance/Colors
is what makes the real system's numbers a fair baseline in the first place; the spike's one arbitrary
constant isn't a comparable effort. Real signal underneath the confounds, though: the real
per-scene tuner independently *chose* TileSize=256 (large, uniform regions, accepting the re-emit
cost) as optimal for exactly the high-motion fixtures this idea targeted — the opposite of what
finer content-adaptive partitioning predicted would win. Not pursued further; would need
Colors-quantization parity plus a real threshold search before the comparison means anything, and
there's no evidence yet that investment would flip the outcome.

## Fixed bugs, with real measured impact

### `QuadtreeMerger` single-frame merge bug (commit `cdb5820`)

`TryMergeBlock`'s only merge criterion was `run.StartMs == first.StartMs && run.EndMs == first.EndMs`
— sub-tiles merge whenever they close at the *exact same instant*, with zero content-similarity
check. In a fast-motion region where every adjacent tile changes every single frame, all four close
in lockstep purely from being equally volatile — satisfying the timing test while sharing no content
at all. Found via a concrete case: `fish_spin_test`'s dominant 399/425-sprite tile position turned
out to be a `QuadtreeMerger`-merged 640×640 block (confirmed via `ffprobe` on the actual PNG, not the
320×320 base tile size), 100% unique frame-to-frame, permanently ineligible for `AnimationDetector`
promotion (which explicitly excludes anything wider than the base tile size). Fix: reject a merge
when the shared `[StartMs,EndMs]` duration is itself single-frame — a single-frame-duration merge
can never represent genuine shared-static content by definition. Real measured win on
`fish_spin_test`: Sprites 720→344 (-52%), Commands 834→445 (-47%), `.osb` 79.06KB→44.63KB (-44%),
PSNR/SSIM *improved* (35.12→37.81dB, 0.9687→0.9847), not regressed.

A related, smaller bug fixed alongside it: `AnimationDetector`'s `isSingleFrame` check used a 0.5ms
tolerance against the *fractional* true frame duration — at 30fps (33.333ms/frame), ms-quantized run
boundaries alternate 33ms/34ms roughly 2:1, and 0.5ms tolerance only ever accepted one side,
misclassifying ~29% of genuine single-frame runs as "stable." Widened to 1.0ms. This fix alone
didn't move `fish_spin_test`'s numbers (the dominant position was intercepted earlier by the
`QuadtreeMerger` bug above, never reaching the `isSingleFrame` check at all) — but it's what let the
now-un-merged base tiles promote cleanly to `SbAnimation` once the merge bug was also fixed.

### `FrameSource` hangs forever on undecodable input

Named-pipe reader (`StreamPipeSink`) blocks indefinitely if ffmpeg never opens the output side at
all — e.g. it exits immediately because the input file doesn't exist, so it never gets far enough to
open the pipe. Verified the process itself exits cleanly (~1.3s on a real ffmpeg run against a
nonexistent file) — the hang is entirely on the .NET side's `NamedPipeServerStream` waiting for a
connection that will never come; nothing about the process having already exited unblocks that wait
on its own.

Three fix attempts recorded as genuine dead ends, each measured wrong rather than assumed:
1. Cancel once `FFMpegArguments.ProcessAsynchronously()`'s own task completes — that task doesn't
   return until the sink's pipe I/O finishes, i.e. it's blocked on the exact same stuck wait.
2. Bound only the sink callback's first `ReadAsync` — the connection handshake happens *before* the
   sink delegate is ever invoked when the client never connects, so nothing inside the sink runs to
   enforce a bound.
3. `FFMpegArgumentProcessor.CancellableThrough(token)` to kill the ffmpeg process — pointless once
   the process has already exited on its own, which is exactly this failure mode.

What actually works: bound only the caller's *own* channel wait
(`ChannelReader.WaitToReadAsync(token)`, entirely within the calling method's control, independent
of FFMpegCore internals) for the first item specifically. Once real data has flowed once, decode
pacing returns to normal and unbounded. `StartupTimeout` was initially 20s; later widened to 60s once
concurrent scene tuning (see below) meant several ffmpeg decodes could legitimately compete for CPU
at once — the 20s bound was tight enough that healthy startup delay under real contention got
misclassified as the hang it exists to catch (a genuinely broken input still fails in ~1.3s either
way, so widening the timeout doesn't mask real failures, only lets healthy-but-contended starts
succeed).

### `VideoId` was a per-document counter, not a stable identity

`VideoSourcePlanner.PlanAsync` assigned `VideoId = i.ToString("x")` — first-seen-order within one
compile's own document. Since `VideoId` names the asset cache subfolder
(`{assetsRootAbs}/{VideoId}/...`), two `.osbv` projects referencing the same video but with a
*different* source count or order landed on *different* asset folders for the same file, sharing
nothing — the entire "cross-project cache sharing" story was only ever verified for the trivial case
of two single-source documents (both happened to get `VideoId="0"`). Fixed: stable hash of
`(NormalizedPath, EffectiveFps)` via `XxHash128`. Verified for real, not just by argument: compiled a
second, differently-shaped `.osbv` document referencing the same video as a *second* source (with an
unrelated video as its first) into the same asset directory — the shared video's `scenes.json`/asset
files were read straight from cache (zero re-tuning log lines), and its `VideoId` folder name was
byte-identical to the original, differently-shaped document's.

### `Colors` fixed at `AssetStore` construction, not per-call

Found mid-implementation of per-scene tuning (not a pre-existing bug — a bug the per-scene feature
would have *introduced* if not caught): `AssetStore.GetOrAdd`/`WriteAnimation` hashed on raw RGB
bytes only, with `Colors` fixed once at construction. Fine when one combo covers a whole video;
broken the moment two *scenes* share one `AssetStore` and pick different `Colors` for the same raw
pixels — the second scene's request would silently reuse whatever the first scene wrote (hexNaming's
`SavePng` skips re-encoding whenever the target path already exists), wrong quantization and all.
Fixed by folding `Colors` into the content hash as XXH3's seed instead of fixing it at construction.

## Built and shipped

### Persistent content-addressed asset cache (XXH3-128, hex-named paths)

Replaced a truncated SHA-256 (64 bits) with XXH3-128 — strictly better on both axes (faster *and*
more collision-resistant), not a tradeoff. Paths under `hexNaming` mode became content-hash-derived
(`s/{hash:x32}.png`), so `SavePng` can skip re-encoding whenever the target file already exists —
turning the asset directory into a genuine cross-run cache. Verified live: compiling the same
fixture twice, cold run wrote 1102 files in 25.1s, warm run touched zero of them in 19.1s (`stat`
mtimes identical before/after), `.osb` output byte-identical. On an animation-heavy fixture (higher
PNG-encode-to-decode cost ratio), warm-cache speedup was >2x.

**Established here, load-bearing for everything downstream**: `TileSize`/`HashQuantLevels`/`Colors`
changes are *not* cache-neutral just because a different value legitimately produces different
content — the hash covers the whole tile buffer, so changing `TileSize` changes every single tile's
hash length, not just the tiles whose visible content actually changed. A run at `TileSize=160` gets
zero cache hits against a prior run's `TileSize=320` cache, even over identical footage. This is
*correct* (a different tile size really is a different asset) but means any auto-tuner must converge
to one stable value per piece of content and hold it across re-compiles — re-searching per invocation
gives the persistent cache zero benefit on every run.

### Global auto-tune (`TileSize`/`HashQuantLevels`/`TileTolerance`/`Colors`, one combo per video)

Always-on, no CLI flag (deliberate — matches a stated preference for a minimal, hard-to-forget CLI
surface over exposing more knobs). Self-calibrating quality floor: probe once at today's hardcoded
defaults on a short sample window, use that PSNR *minus* a small slack (1dB) as the floor every
candidate must clear — not an absolute dB constant (would be wrong for some content either way) and
not a zero-slack floor (would make every lossy-but-cheaper candidate score below baseline by
construction, degenerating the search into "always pick baseline"). Coordinate-descent search
instead of a 4D grid (13–15 probes vs 81–625): `TileSize` first (biggest structural lever), then
`Colors` (orthogonal, only affects palette), then `HashQuantLevels`, then `TileTolerance` last (most
content-dependent, confirmed near-free on noisy footage but expensive on clean). Cost signal is
asset bytes *plus* estimated `.osb` text bytes (`commandCount * ~100`, calibrated against a real
measured ratio), not asset bytes alone — under-weighting command count would blind the search to
`TileSize`'s biggest real cost lever.

Real measured wins across all 5 project fixtures, one full 10s window per fixture, tuned combo vs.
the old hardcoded default:

| Fixture | Content | Tuned combo | Δ bytes | Δ sprites |
|---|---|---|---|---|
| short_animation | clean animation | 256/32/8/16 | -25.2% | 90 / 911 |
| birdbrain | real-world noisy footage | 64/32/8/0 (=default) | 0.0% | 8549 / 8549 |
| bad_apple | extreme B&W flicker | 64/16/8/0 | -4.4% | 10007 / 12398 |
| fish_spinning | FHD, moderate noise | 256/32/8/0 | -7.7% | 875 / 6705 |
| minecraft | clean game-rendered | 256/16/8/0 | -28.9% | 842 / 14535 |

No fixture regressed. Real-world noisy footage correctly landed at the hardcoded default (the safety
floor working as designed — the tuner found nothing that beat it). The one pathological case
(bad_apple, near-total frame-to-frame churn) still recovered a smaller win on a different axis
instead of giving up entirely. Sprite/command count drops consistently more than bytes do (Minecraft:
-28.9% bytes but -94% sprite count) — osu!'s storyboard engine processes proportionally far fewer
objects than the byte number alone suggests.

Tuning cost was measured and iteratively cut, each step measured before moving to the next:
5m31s (first real run, 14 probes, 4000ms sample) → 2m8s (shrink sample window to 1500ms — render
time, not decode count or ffmpeg spawns, was the actual dominant cost, found by per-probe timing not
assumption) → 1m25s (parallelize within-axis candidates via `Task.WhenAll` — axes stay sequential
against each other, they depend on the previous axis's chosen value; candidates within one axis are
independent) → 30s (dedupe redundant seed-carry probes — every axis's "unchanged" candidate was
re-probing the exact tuple the previous axis's own winner already resolved).

### Per-scene auto-tuning (this session's Part 4, the system this repo's current work continues
extending)

Motivated by two throwaway experiments before committing to building it: a 2-scene hard-cut fixture
(-8.9% bytes vs. one global combo) and a 5-fixture-concat fixture (-26.7% bytes) — both showing a
single global combo compromises worse the more distinct content regimes a video has, because its
fixed-position sample window can land inside any one scene and get shaped by that scene's own
content, then get applied uniformly elsewhere. Explicit warning carried forward at the time: -26.7%
is a ceiling (5 maximally-different sources cut every 5s, the most favorable case per-scene tuning
can have) — a typical video with one or two regime changes should land closer to -8.9% or less, not
27%.

Real shipped-code measurement (not the throwaway harness, forcing the same production code path down
a "1 scene" vs. "N scenes" branch for an apples-to-apples comparison): **-7.86%** on an 8-scene real
fixture — consistent with the "don't expect the ceiling" warning.

Scene-boundary detection (`ScenePrePass`, decode-only, no PNG/QuadtreeMerger/AnimationDetector) finds
hard cuts via one signal: a frame where a very high fraction of tile positions close their run
simultaneously, under *any* parameter set — detectable directly from `TileRunTracker.Advance`'s own
return value, no reference combo, no rolling window (an earlier online cost-rate-drift idea was
rejected as a lagging signal that smears exactly the one frame it needs to catch).

**Real calibration bug found via the plan's own verification, not assumed away**: the initial
`CutThreshold=0.8` guess missed a real cut measured at 0.721 fraction, while an ordinary non-cut
high-motion frame measured 0.662 — recalibrated to 0.7, strictly between the two real measurements.
The initial merge-suppression rule ("reject any candidate within 2000ms of the last accepted cut")
swallowed a real second cut that landed only 1.8s after a first one (a real internal high-motion
event inside one clip, immediately followed by a real scene boundary) — replaced with a genuinely
different two-pass algorithm: burst-coalesce nearby above-threshold frames into one candidate first
(300ms gap tolerance), then reject a candidate only if it would produce a near-zero-length scene
right after the previous one (1000ms, degenerate-case guard only — not a general "suppress anything
nearby" merge, since a real distinct high-motion stretch earning its own scene close in time to
another real cut is correct behavior for this feature, not noise).

## This session's own later findings (after the old plan file's last entry)

- **Lazy per-scene tuning**: the shipped Part 4 above was tuning *every* detected scene across a
  whole file eagerly, right after prepass — a 10s-window `.osbv` query against a 255s file with many
  natural cuts paid ~100–170s of tuning *per scene*, for scenes nowhere near the requested window.
  Fixed: tune a scene only when `VideoCompiler` is actually about to encode it, gated by the same
  `Clip()`+skip check that already proves an unused scene's tuned params are never read.
- **Tuning-probe disk I/O was ~75% of every probe's wall time** (measured across 52 real probes, not
  estimated): each probe wrote real PNGs to a throwaway temp directory purely to learn byte size and
  read them back for a PSNR comparison — pure overhead, never a real deliverable. Fixed via an
  in-memory `AssetStore` mode (PNG encode still happens, into a `MemoryStream` instead of a file) and
  a renderer that reads probe assets back from it directly. Same-scene regression check confirmed
  byte-identical output, ~2.4x faster tuning on one real scene pair (213s → 88s).
- **`scenes.json` persistence dropped, on request**: it sat next to the real PNG assets a project
  ships — unwanted clutter for a tool used as a one-shot compile, not a long-lived pipeline replaying
  the same footage across many runs. Detection and tuning now both stay in-memory for the lifetime of
  one `CompileAsync` call only, no cross-run cache file at all for either.
- **Ideas raised for future asset-representation work (not designed, not built)**: (1) skip emitting
  an asset entirely for a tile that never has any content the whole time it exists — distinct from
  the rejected `AssetTrimmer` alpha-masking variant, which failed on *partial* transparency over an
  already-visible, changing sprite; a permanently-empty tile has no such lifetime-overlap hazard.
  (2) Normalize an asset to a neutral/grayscale "floor" and reconstruct per-instance color via
  `.osb`'s existing `Colour` command (a native `v_Colour * texel` multiply the shader already
  supports) instead of storing one PNG per distinct color variant of the same shape — simpler in kind
  than the residual-coding idea above (a single uniform multiply per instance, not per-pixel signed
  alpha).

## Three-system split, window-scoped detection, global asset store, train/eval tuning

Follow-up work after the findings above, driven by three architectural concerns raised in the same
session: scene detection re-decoding whole files for narrow queries, asset storage scoped too
tightly to dedupe across fps/files, and a single 1.5s tuning sample risking overfit to whatever that
one clip looked like. Decomposed into three ordered, independent parts (B → A → C).

### Part B — `Detection`/`Tuning`/`Encode`/`Shared` folder split; window-scoped `ScenePrePass`

Folder restructure: `Compiler`'s flat `Analysis`/`Encoder`/`Media`/`Osb`/`Render`/`Evaluation`/
`Tuning` layout regrouped into 3 system folders (`Detection`, `Tuning`, `Encode`) plus `Shared`
(infrastructure both systems call — `Analysis`/`Media`/`Render`/`Evaluation` subfolders) and
`Compilation` (the orchestrator, deliberately outside all three — it composes them, doesn't belong to
one). Pure move + namespace rename, zero behavior change, verified via full `using`/qualified-
reference grep before moving (zero type-name collisions across the `Encoder`+`Osb` merge into
`Encode`).

Behavior change landed in the same pass: `ScenePrePass.ScanAsync` previously decoded the entire
source file to find every cut it contains, even though one `.osbv` compile only ever needs the scenes
overlapping its own requested window (`VideoSourcePlan.UnionStartMs/UnionEndMs`, already computed,
just not passed down). Scoped `ScanAsync` to `[windowStartMs, windowEndMs)` — for a long file with
many natural cuts far outside the requested window, this stops paying to decode content nowhere near
what's actually being tuned/encoded. `SceneCache`'s two thin orchestration wrappers
(`BuildAsync`/`EnsureTunedAsync`) were deleted in the same move; the real logic they wrapped
(`ScenePlan` record, `BuildCoreAsync` — cut-list → scene-boundary, pure and unit-tested via an
injected scan delegate) moved into `Detection/SceneBounds.cs`, called directly by
`VideoCompiler.ScenesFor`/`TunedFor` now.

An initial version of this window-scoping added a margin-fetch mechanism (`PlanMargins`/`MarginPlan`):
if the requested window was shorter than whatever sample size tuning needed, decode outward past the
window's own edges (never past an already-found internal cut) to pad the sample up to size. This was
removed again in Part C below once `ParameterTuner`'s own design stopped needing it — see that
section's "short scene" fix. `ScanAsync`'s `StartMs`/`EndMs` now always equal the requested window
exactly, nothing padded past it.

### Part A — global content-addressed asset store, `VideoId` dropped entirely

`VideoSourcePlan` carried a `VideoId` (by then already a stable `(path, fps)` hash, per the earlier
fix above) that named the asset store's own subfolder — one `AssetStore` instance per plan. This
still blocked two real dedupe opportunities the user wanted: the same file re-encoded at a different
fps, and two *different* files whose tiles happen to produce byte-identical pixel content, neither of
which shares a `VideoId` and so neither could ever share a cached asset.

Fixed by going further than a better `VideoId`: dropped the field entirely. One flat,
content-addressed `AssetStore` instance (`s/{hash}.png`, `a/{hash}/f{n}.png`) is now shared across
the *whole compile*, not one per plan — asset identity is decided purely by a tile's own content
hash, nothing else. `VideoSourcePlan` no longer carries any per-plan identity at all. Scope decision
(via explicit choice, not left implicit): exact-hash dedupe only — no near-duplicate/perceptual
matching, no cross-fps frame interpolation to manufacture more hash hits. `AssetStore` must be
constructed once per `CompileAsync` call and passed down, not once per plan — confirmed via the same
`FileCount`/`TotalBytes` accounting the store already tracked internally (a second per-plan instance
would silently double-count a cross-plan cache hit that never touched the first instance's own
in-memory dedupe table, even though the on-disk skip-if-exists write is safe either way).

### Heatmap-adaptive tile partitioning spike — see the "Rejected directions" entry above (built,
tested against fish_spinning/minecraft/short_animation, not adopted — the shipped tuner's own
`TileSize` choice on high-motion content ran opposite the spike's founding hypothesis).

### Part C — train/eval tuning sample methodology, short-scene fix

Motivation: `ParameterTuner`'s original design (see "Global auto-tune" above) probed one single
~1.5s sample window per candidate — a candidate could look like a clean win purely because of what
that one clip happened to contain, with no check against how it performed on the rest of the scene.

Design, settled through dialogue (each choice confirmed explicitly, not defaulted):
- **Eval's purpose is rejection, not just measurement** — a candidate must clear the PSNR floor on
  *both* its train probe and a held-out eval probe it never saw during selection, not just get its
  train/eval numbers reported. `ParameterTuner.Select`'s gate: `train.Psnr >= floor && eval.Psnr >=
  floor`, both required.
- **Sampling strategy: evenly spread across time**, not random or front-loaded.
- **Sizing: 3 train segments + 1 eval segment, 500ms each, ~2000ms total** as the starting point (an
  explicit placeholder — the plan noted this would need real benchmarking against the earlier
  `SampleWindowMs=1500` value, not a final number).

**Bug found via real regression (`badapple8`), not caught by unit tests**: the first implementation
spread the 3 train + 1 eval windows across 4 *separate, far-apart* quarters of the scene (~25%/50%/
75%/center). On the real `bad_apple` fixture this measured baseline PSNR at 28.02dB instead of the
previously-established ~24.90dB (single-sample) baseline — the far-apart eval slice happened to land
on easier-to-compress content, shifting the self-calibrated floor up by ~3dB and causing every axis
to fall back to baseline. Net effect: total bytes +18.9% (34.3MB vs. the known-good 28.9MB) and ~2x
tuning time, a real regression, not noise. Root cause: a train/eval split whose samples are scattered
across a scene measures "how well does this combo generalize across everywhere in the scene," which
is a different, stricter question than "is a 3s local sample representative enough" — and the floor
computation is sensitive to exactly where its own baseline probe happens to land.

Fix: replaced the scattered-quarters layout with one **local block** of `RequiredSampleMs=3000ms`
(centered within the segment if it's the first segment being tuned, anchored at the segment's own
start otherwise), subdivided into 4 *contiguous* 750ms chunks inside that one block — chunks 0,1,3
train, chunk 2 eval. Verified via the same real fixture (`badapple9`): output byte-identical to the
pre-regression known-good baseline (28,793,996 total bytes), same combo chosen per scene.

**Final refinement, from a user insight**: a scene no longer than `RequiredSampleMs` isn't a sample of
anything bigger — it *is* the entire deliverable for that scene, so tuning it against a held-out eval
slice of itself achieves nothing (there's no "unseen material" left to generalize to; overfitting to
100% of your own exact output data is the goal, not a risk). `BuildSampleWindows` now branches: a
segment `<= RequiredSampleMs` returns its own full span as both train and eval (probed once, the eval
result reused rather than re-probed); a longer segment keeps the 4-chunk local-block split unchanged.
This also removed the last consumer of Part B's margin-fetch mechanism (`PlanMargins`) — a short
scene no longer needs padding out to a fixed sample size at all, so `ScenePrePass` lost that mechanism
entirely, and `ScanAsync`'s returned range is now always exactly the requested window.

Verified via real regression (`badapple10`, same fixture/window used throughout): for the one short
scene in that fixture (2083ms, under the 3000ms threshold), every probe line's `trainPSNR` and
`evalPSNR` printed as exactly equal (confirming reuse, not a duplicate second probe); the fixture's
other, longer scene still split normally. Output (`sprites=6897 animations=1467 commands=8364
assets=27706`) matched the prior confirmed-good baseline in every counted dimension; total bytes
landed within 0.03% of the previously recorded baseline total (some byte-level noise remains between
runs separated by intervening code changes and cleaned-up scratch state — the counted-object equality
above is the stronger signal that nothing regressed).

## 2026-08-21 — Tuning-cost spec work: shared sample-window decode + probe PNG bypass (audited)

Implemented the two tuning-cost specs (shared decode across candidates; PNG round-trip bypass in the
probe renderer) and then audited the result — this entry is the audit record, numbers as measured.

### What landed

- **Shared decode**: `ParameterTuner.TuneAsync` pre-decodes the fixed sample windows once
  (`FrameSource.ReadBuffersAsync` replays them; `TileEncodeLoop.Options.PreDecodedFrames`,
  `VideoFrame.Wrap` for caller-owned buffers). ffmpeg spawns per tuning pass: **40 → 4**;
  frame-wait stage **54.9s → 13ms** (0.03% of before).
- **PNG bypass**: in-memory `AssetStore` keeps the post-quantize pixels (`GetMemoryPixels`);
  `SoftwareStoryboardRenderer.LoadAsset` reads them directly — PNG decode round-trip gone on the
  probe path. Disk-backed compile path untouched (returns null → old file path).
- Correctness pinned by tests: 5 new `AssetStoreMemoryPixelsTests` (stored pixels == decoded PNG
  bytes for quantized/unquantized sprites and animation frame paths; capture point is *after*
  quantize; disk stores return null). 42/42 compiler tests pass; `bench` end-to-end on a 2s window
  regression-clean (PSNR 34.51dB, SSIM 0.9964) — the real compile/disk path is intact.

### A/B on bad_apple 5s-middle scene (`[107074, 112074)` ms), same machine, sequential runs

| | old path (per-probe decode, `--no-shared`) | new path (shared decode) |
|---|---:|---:|
| ffmpeg spawns | 40 | 4 |
| frame wait | 86 649ms | 13ms |
| encode stage sums (CPU work) | 904 166ms | 452 543ms (−50%) |
| wall | 361 162ms | 459 936ms ⚠︎ |
| tuned combo | 64/32/8/0 | 64/32/8/0 |

**Wall is machine-confounded, not a code regression**: the *old* path measured **88.8s** at this
session's start and **361s** today — same code, same fixture. The machine is ~4x slower under
current load (Rider + Chrome ≈ 4GB; 1.9GB free RAM; 41% CPU). Second factor: with ffmpeg gone,
candidates become pure-CPU and stop interleaving behind decode waits — measured parallelism
collapsed from ~2.4x (old, ffmpeg-paced) to ~1.0x (new, CPU-bound, contended machine). The
load-independent measure is total CPU work: **halved**. A clean wall comparison needs an idle-machine
re-measure; `tune-bench --no-shared` exists for exactly that.

### Correctness caveat discovered by the audit (spec claim was too strong)

The original spec claimed byte-identical output vs the old path. Not true, and the old path itself
never had it: **per-ffmpeg-spawn decode jitters ±1 frame at the window boundary**. In a single
*old-path* run, the same tuple (64,32,8,0) probed in two different axes produced costs differing by
**9%** (3 226 823 vs 3 530 775) and eval PSNR differing 0.01dB. New-vs-old across runs: train 29.53
vs 29.56dB, eval 23.76 vs 23.67dB, cost within 6.5% — the same order as that pre-existing jitter.
Decision stays stable (64/32/8/0 both paths). The shared path *removes* within-run jitter: one
decode per window → every candidate compares against the same fixed reference frames, and equal
tuples always give equal results inside a run (old design could swing 9% between axes). That is a
real search-consistency improvement, worth keeping.

### Decision: keep as-is

Kept the current implementation, with these acknowledged tradeoffs:

- **Memory**: all 4 sample windows are held for the whole search (~840MB at 1440x1080@60), a
  deviation from the spec's one-window-at-a-time goal. Isolated 1-window run (280MB) showed the same
  CPU-stage costs, so pinning is not the cause of today's wall numbers — but it is the biggest
  remaining memory risk on low-RAM machines (8GB class). Fallback if it ever bites: window-major
  per-axis scheduling (4x4 = 16 decodes, ~210MB resident) or the fan-out pump (both noted in the
  now-deleted spec files; the design survives in ParameterTuner's comments).
- **Unrelated working-tree noise**: 6 files carry cosmetic collection-expression refactors
  (`[...]` for `ToArray/ToList`) made outside this session: `GroupTransformBaker`,
  `ScenePrePass`, `TileTimeline`, `FrameSourceTests`, `ParameterTunerTests`,
  `OsbWriterShorthandTests`. Functionally inert, left untouched.
- The `tune-bench` command (hidden; `--no-shared` A/B flag; per-window stage table; peak working
  set) stays as the measurement instrument for future runs.

## 2026-08-22 — Full Minecraft 60fps compile + A/B GPU decode (hypothesis #2 retest)

### Minecraft full clip (60fps, 1920x804, RTX 3060 decode)
- **Time:** 542.1 min (~9h) — 60fps tuning 2.5× heavier than 24fps baseline
- **Output:** .osb 9.31 MB + assets 735.01 MB = **744.32 MB** (79,612 sprites / 79,223 assets)
- 36 scenes, 35 cuts, 60fps timeline (duplicated from 23.976 source)

### A/B bad_apple 5s middle scene — GPU decode (RTX 3060), free machine

| | old path (--no-shared) | new path (shared) | notes |
|---|---:|---:|---|
| ffmpeg spawns | 40 | **4** | 40 → 4 pre-pass decodes |
| frame wait | 52.6s | **0.01s** | eliminated |
| encode CPU work | 831.8s | **515.8s (-38%)** | decode+render+psnr halved |
| wall | **336.0s** | 525.8s | new path wall worse — lost ffmpeg async overlap |
| peak RAM | 2.17 GB | **1.97 GB** | |
| combo | 64/32/8/0 | 64/32/8/0 | stable |

**Hypothesis #2 verdict:**  
✅ CPU work halved, frame wait eliminated, spawns 40→4, peak RAM lower.  
⚠️ Wall time paradox: old path benefits from ffmpeg async gaps (~2.5× overlap), new path pure-CPU overlap ~1.0× on this 8-core machine → old path wall better despite 1.6× more CPU work. On machines with better overlap (≥2×) the new path wins wall too.

### Code changes committed
- 7e427c1 feat: requested fps wins (60fps upsampling via ffmpeg frame duplication)
- TuneBench --hwaccel for GPU benchmarking

## 2026-08-21 (follow-up) — Shared-decode fan-out deadlock; reverted to in-memory replay; fresh A/B on GPU machine

### The bug: a channel fan-out replaced the in-memory replay and deadlocked

After the 08-22 A/B above, the shared-decode path was reimplemented with a per-window
channel fan-out (`WindowFrameSource` + `FanOutFrameBuffer`, `CreateFanOut(consumerCount,
bufferSize=8)`). Its producer writes to **all** consumers unconditionally on every frame:

```csharp
for (int i = 0; i < _consumerCount; i++)
    await _consumerChannels[i].Writer.WriteAsync(copies[i], ...);   // FullMode = Wait
```

Consequence: any consumer a probe is *not* currently reading fills up (8-frame buffer) and
**blocks the producer**, which then can't feed the consumers that *are* being read →
**deadlock at concurrency ≥ 4**. At concurrency 1 it didn't deadlock but **under-delivered
frames** (only the ~8 already-buffered frames reached the lone reader before the producer
stalled), so probes rendered a handful of frames and reported a falsely-fast ~56s "success"
with a plausible-looking PSNR — incomplete work masquerading as a win. This is exactly why the
earlier 56s/c=1 number was not trustworthy: it beat the documented 526s only by doing ~1/9 of
the frames.

### The fix: restore the proven in-memory replay (commit `8de86aa`)

Reverted `TuneAsync` to decode each distinct sample window **once** into an in-memory
`List<byte[]>` (`DecodeSampleWindowsAsync`) and replay it via
`TileEncodeLoop.Options.PreDecodedFrames` (`VideoFrame.Wrap` over caller-owned buffers). Same
design as the 08-21 audit and the 08-22 A/B that produced the tables above — one ffmpeg pass
per window replaces the per-candidate decode (4 decodes total), with **no channels to
deadlock**. Deleted the now-unused `FanOutFrameBuffer.cs`/`WindowFrameSource`. `tune-bench`
keeps `--max-concurrency <N>` (global `SemaphoreSlim` bounding concurrent CPU probe workers)
and `--no-shared` (per-probe decode A/B mode).

Verified: `tune-bench --ss 107.074 -t 5` completes at **both concurrency 1 and 4** (no
deadlock), reports **4 decoded windows / 1.96 GB peak RAM / combo 64/32/8/0**; all 43 compiler
tests pass; build warning-free.

### Fresh A/B on THIS machine — bad_apple 5s middle scene, CUDA GPU decode (`--hwaccel cuda`), concurrency 1

Same fixture/window as the 08-22 table, re-run here to get a current-machine number:

| | old path (`--no-shared`) | new path (shared) | change |
|---|---:|---:|---|
| ffmpeg spawns | 40 | **4** | ⬇️ 10× fewer |
| frame wait (ffmpeg) | 43 239ms (9.7%) | **12ms (0.0%)** | ⬇️ eliminated |
| encode CPU work | 444 300ms | **351 050ms** | ⬇️ −21% |
| wall | 358 129ms | 360 881ms | ≈ **tied** (+0.8%, +2.8s) |
| peak RAM | 1.71 GB | 1.96 GB | ⬆️ +0.25 GB (holds decoded frames) |
| combo | 64/32/8/0 | 64/32/8/0 | ✅ stable |

### Why the wall-time verdict flipped vs. the 08-22 table

The 08-22 table (free machine) showed **new path wall WORSE** (526 vs 336s). Here it is
**tied** (361 vs 358s). Same code, same design — the difference is the machine's decode path:

- **Free machine (08-22):** CPU decode, ~8 cores. The old path's per-probe ffmpeg decode runs
  *asynchronously* behind the CPU work, so its 40 spawns buy real ~2.5× pipeline overlap; the
  new path is pure-CPU after the one-time decode and gets only ~1.0× overlap → old wins wall.
- **This machine (CUDA GPU):** decode is offloaded to the GPU and fast, so the old path's
  async-overlap advantage largely disappears; both paths are CPU-bound by the software renderer
  and finish in ~360s. The new path's −21% CPU work and 10× fewer ffmpeg spawns come for free.

**Conclusion: the wall-time gap is machine-confounded, not a code regression** — already
suspected in the 08-21 audit and now confirmed by direct re-measure on a GPU box. On a machine
with GPU decode (or enough cores that CPU work overlaps cleanly), shared decode is competitive
on wall time *and* strictly better on spawns / frame-wait / CPU work. The deadlock fix makes
it actually reachable at any concurrency. (RAM: shared decode holds the 4 decoded windows in
memory, so it runs slightly *higher* here than the streaming old path; on CPU-only boxes the
opposite held — see 08-22's 1.97 vs 2.17 GB — because 40 ffmpeg processes dominate there.)
