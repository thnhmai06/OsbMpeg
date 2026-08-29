# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OsbMpeg compiles a video into an osu! storyboard (`.osb` + PNG assets) using a tile-grid
conditional-replenishment codec: the canvas is cut into a fixed grid, each tile position tracks its
own "run" (content unchanged since some start time, by quantized-hash equality), and each closed run
becomes one `Sprite` (or, if it never repeats content, one frame of an `Animation`). `.osb` has no
predictive/residual coding, so every emitted asset is a full self-contained PNG crop and dedupe
happens only through content-hash equality across runs.

Input is `.osbv`, a handwritten superset of `.osb`'s own command grammar that adds `AnimationVideo`,
a group-transform object naming a video file, expanded at compile time into many tile
sprites/animations auto-cover-placed at its declared `(X,Y)`, with the object's own commands baked
into every generated tile (`GroupTransformBaker`). Native `Sprite`/`Animation` objects in a `.osbv`
pass straight through to output IR unchanged.

## Read this before designing anything

`docs/direction.md` is the single source for where this project is going: what is broken in the
current implementation and why, what osu! storyboards can actually express, the representation
space, the target architecture with its invariants, a step-by-step implementation roadmap
(M0 through M3.5), open questions, and the full measurement history of everything already tried.

Two things that document settles, and that cost real time to re-derive:

- The current output is blurry and heavy for structural reasons, not tuning reasons. The quality
  floor is relative (`ParameterTuner.cs`), palette quantization has no dithering (`AssetStore.cs`),
  the run tracker is open-loop so slow changes never close a run, and there is exactly one output
  primitive (a static crop plus one `S` command), so any camera motion re-emits the whole canvas
  every frame.
- Several plausible directions are already ruled out with measurements, including motion/residual
  compensation against a fixed grid, alpha-masked asset trimming, an RDO selection layer, and
  heatmap-adaptive tile partitioning. Check section 11 (decision table) and section 13 (measurement
  history) before proposing any of them again.

## Commands

Build:

```
dotnet build -c Release
```

Run tests. Do not use `dotnet test`, because the `Microsoft.Testing.Platform` runner set in
`dotnet.config` fails to invoke on this .NET 10 SDK. Run the built test executable directly:

```
dotnet build -c Release
cd tests/OsbMpeg.Compiler.Tests/bin/Release/net10.0 && ./OsbMpeg.Compiler.Tests.exe
cd tests/OsbMpeg.Parsers.Tests/bin/Release/net10.0 && ./OsbMpeg.Parsers.Tests.exe
cd tests/OsbMpeg.Cli.Tests/bin/Release/net10.0 && ./OsbMpeg.Cli.Tests.exe
```

Filter to one test (xUnit v3 in-process runner flags):

```
./OsbMpeg.Compiler.Tests.exe -method "*BuildSampleWindows_SceneShorterThanRequiredSampleMs*"
```

Run the CLI. The default and only public surface is `compile`; `decode`, `bench`, `probe`,
`inspect`, and `tune-bench` are hidden regression instruments, still callable by name, not the
product surface:

```
dotnet run --project src/OsbMpeg.Cli -- <input.osbv> <output.osb> <assets-dir> [--hwaccel MODE]
```

Requires `ffmpeg` and `ffprobe` on `PATH` (via FFMpegCore) for any video decode path.

## Architecture

Three projects, strictly layered (`Cli` → `Compiler` → `Parsers`, no back-references):

**`OsbMpeg.Parsers`** is the format layer and knows nothing about video or codecs. `Osbv/` parses the
`.osbv` source (recursive descent over indentation, arbitrary-depth `L` nesting via a depth stack;
see `OsbvParser`'s doc comment). `Osb/` reads and writes real `.osb` files. `Ir/` is the shared
command IR (`SbDocument`, `SbObject`, `SbCommand`) that both formats speak, plus `Ir/Passes/` (loop
flatten and extract, no-op drop, adjacent-command merge) applied to output IR before writing.
`Render/` evaluates commands into concrete per-frame state, used by the software renderer for PSNR
probing.

**`OsbMpeg.Compiler`** is the codec, split into three systems plus shared infrastructure and an
orchestrator, where a folder is a system boundary:

- `Detection/` finds hard-cut scene boundaries inside a requested time window only, not the whole
  source file, via `ScenePrePass`: decode at a fixed baseline combo and watch for a frame where
  almost every tile position's run closes simultaneously. That signal is combo-independent and needs
  no reference render. `SceneBounds.BuildCoreAsync` turns a cut list into a boundary list as pure
  logic separate from the real decode (`ScanAsync`), so it is unit-testable without ffmpeg.
- `Tuning/` picks `TileSize`, `HashQuantLevels`, `TileTolerance`, and `Colors` per scene via
  coordinate descent (axes ordered biggest-lever-first), self-calibrated against a floor (today's
  hardcoded combo's own measured PSNR minus a slack), gated on both a train sample and a held-out
  eval sample so a candidate that overfits the train clip gets rejected. A scene no longer than
  `RequiredSampleMs` skips the eval split and tunes against its own full span, since that scene is
  the deliverable rather than a sample of something bigger (see `BuildSampleWindows`). Runs lazily,
  only for a scene `VideoCompiler` is actually about to encode. Note that `docs/direction.md`
  retires this whole system: the relative floor it optimizes toward is the root cause of the blur.
- `Encode/` has `TileEncodeLoop`, the shared decode, track, merge, detect, emit loop used by both the
  `.osbv` per-`AnimationVideo` path and the legacy whole-canvas `EncodePipeline` and `bench` path.
  `AssetStore` is the content-addressed PNG store: one flat, hash-named (`s/{hash}.png`,
  `a/{hash}/f{n}.png`) instance shared across the entire compile rather than per video source, so two
  scenes (even from different source files) that produce byte-identical tile content share one file.
  It supports an in-memory mode (PNG bytes into a `MemoryStream`, no disk I/O) used by
  `ParameterTuner`'s probes.
- `Shared/` is infrastructure the other systems depend on: `Analysis/` (`TileGrid`, `TileRunTracker`,
  `TileRun`, `QuadtreeMerger` which merges adjacent tiles that closed in lockstep into one bigger
  asset, `AnimationDetector` which upgrades an every-frame-changing tile run into one `Animation`
  instead of N sprites, and `ContentHasher` using XXH3-128), `Media/` (`FrameSource` ffmpeg decode,
  `FrameWriter`, `MediaProbe`), `Render/` (`SoftwareStoryboardRenderer`, which replays IR to a pixel
  buffer for PSNR comparison with no real `.osb` round-trip, and `Compositor`), and `Evaluation/`
  (`Metrics.Psnr`).
- `Compilation/` is the orchestrator (`VideoCompiler.CompileAsync`), deliberately outside all three
  systems since it composes them. It groups `AnimationVideo` objects into `VideoSourcePlan`s by
  `VideoSourceKey` (deduping shared decode when several objects reference the same file and window),
  drives detection, tuning, and encode per scene, and bakes group transforms
  (`GroupTransformBaker`) into each `AnimationVideo`'s generated tiles.

**`OsbMpeg.Cli`** is Spectre.Console.Cli command wiring only, with no codec logic.

## Conventions worth knowing

`GroupTransformBaker` owns position and scale for any object it bakes, and it emits constant
`MoveX`/`MoveY`/`VectorScaleX`/`VectorScaleY` on every tile whenever the group's position is
constant. Anything else that wants to drive those properties on the same object has to compose
through the baker rather than emit a second track: stable and lazer resolve overlapping same-property
commands differently and permanently (ppy/osu#7257), and nothing in the current pipeline detects it.
`MergeAdjacentCommands` only checks adjacency, `OsbValidator` only compares object counts, and
`CommandEvaluator` resolves overlaps as last-in-list-wins, which matches neither client.

Changing `TileSize`, `HashQuantLevels`, or `Colors` invalidates the whole asset cache, not just the
tiles whose visible content changed, because the hash covers the entire tile buffer.
