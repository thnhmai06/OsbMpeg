# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OsbMpeg compiles a video into an osu! storyboard (`.osb` + PNG assets) using a tile-grid
conditional-replenishment codec: the canvas is cut into a fixed grid, each tile position tracks its
own "run" (content unchanged since some start time, by quantized-hash equality), and each closed run
becomes one `Sprite` (or, if it never repeats content, one frame of an `Animation`). `.osb` has no
predictive/residual coding — every emitted asset is a full self-contained PNG crop, dedupe happens
only through content-hash equality across runs (see `docs/research.md` for why motion-vector/residual
approaches were tried and rejected).

Input is `.osbv`, a handwritten superset of `.osb`'s own command grammar that adds `AnimationVideo` —
a group-transform object naming a video file, expanded at compile time into many tile
sprites/animations auto-cover-placed at its declared `(X,Y)`, with the object's own commands baked
into every generated tile (`GroupTransformBaker`). Native `Sprite`/`Animation` objects in a `.osbv`
pass straight through to output IR unchanged.

## Commands

Build:
```
dotnet build -c Release
```

Run tests — **do not use `dotnet test`** (`Microsoft.Testing.Platform` runner set in
`dotnet.config` fails to invoke on this .NET 10 SDK); run the built test executable directly:
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

Run the CLI (default/only public surface is `compile`; `decode`/`bench`/`probe`/`inspect` are hidden
regression-instrument subcommands, still callable by name, not the product surface):
```
dotnet run --project src/OsbMpeg.Cli -- <input.osbv> <output.osb> <assets-dir> [--hwaccel MODE]
```

Requires `ffmpeg`/`ffprobe` on `PATH` (via FFMpegCore) for any video decode path.

## Architecture

Three projects, strictly layered (`Cli` → `Compiler` → `Parsers`, no back-references):

- **`OsbMpeg.Parsers`** — format layer, no video/codec knowledge. `Osbv/` parses the `.osbv` source
  (recursive-descent-over-indentation, arbitrary-depth `L` nesting via a depth stack — see
  `OsbvParser`'s doc comment). `Osb/` reads/writes real `.osb` files. `Ir/` is the shared command IR
  (`SbDocument`/`SbObject`/`SbCommand`/...) both formats speak, plus `Ir/Passes/` (loop
  flatten/extract, no-op drop, adjacent-command merge) applied to output IR before writing. `Render/`
  evaluates commands into concrete per-frame state (used by the software renderer for PSNR probing).
- **`OsbMpeg.Compiler`** — the codec, split into 3 systems + shared infrastructure + an orchestrator
  (folder = system boundary):
  - **`Detection/`** — finds hard-cut scene boundaries inside a requested time window only (not the
    whole source file) via `ScenePrePass`: decode at a fixed baseline combo, watch for a frame where
    almost every tile position's run closes simultaneously (that signal is combo-independent, no
    reference render needed). `SceneBounds.BuildCoreAsync` turns a cut list into a boundary list, as
    pure logic separate from the real decode (`ScanAsync`), so it's unit-testable without ffmpeg.
  - **`Tuning/`** — `ParameterTuner` picks `TileSize`/`HashQuantLevels`/`TileTolerance`/`Colors` per
    scene via coordinate-descent (axes ordered biggest-lever-first), self-calibrated against a
    floor (today's hardcoded combo's own measured PSNR minus a slack), gated on both a train sample
    and a held-out eval sample so a candidate that overfits the train clip gets rejected. A scene no
    longer than `RequiredSampleMs` skips the eval split entirely and tunes against its own full span
    (that scene IS the deliverable, not a sample of something bigger — see `BuildSampleWindows`).
    Runs lazily, only for a scene `VideoCompiler` is actually about to encode.
  - **`Encode/`** — `TileEncodeLoop` is the shared decode→track→merge→detect→emit loop (used by both
    the `.osbv` per-`AnimationVideo` path and the legacy whole-canvas `EncodePipeline`/`bench` path).
    `AssetStore` is the content-addressed PNG store: one flat, hash-named (`s/{hash}.png`,
    `a/{hash}/f{n}.png`) instance shared across the *entire* compile, not per video source — two
    scenes (even from different source files) that happen to produce byte-identical tile content
    share one file. Supports an in-memory mode (PNG bytes into a `MemoryStream`, no disk I/O) used by
    `ParameterTuner`'s probes.
  - **`Shared/`** — general infrastructure both `Detection` and `Encode` (and `Tuning`) depend on:
    `Analysis/` (`TileGrid`, `TileRunTracker`/`TileRun`, `QuadtreeMerger` — merges adjacent tiles that
    closed in lockstep into one bigger asset, `AnimationDetector` — upgrades an every-frame-changing
    tile run into one `Animation` instead of N sprites, `ContentHasher` — XXH3-128), `Media/`
    (`FrameSource` ffmpeg decode, `FrameWriter`, `MediaProbe`), `Render/` (`SoftwareStoryboardRenderer`
    — replays IR to a pixel buffer for PSNR comparison, no real `.osb` round-trip needed), `Evaluation/`
    (`Metrics.Psnr`).
  - **`Compilation/`** — the orchestrator (`VideoCompiler.CompileAsync`), not "inside" any one system.
    Groups `AnimationVideo` objects into `VideoSourcePlan`s by `VideoSourceKey` (dedupes shared decode
    when multiple objects reference the same file+window), drives Detection → Tuning → Encode per
    scene, and bakes group transforms (`GroupTransformBaker`) into each `AnimationVideo`'s generated
    tiles.
- **`OsbMpeg.Cli`** — Spectre.Console.Cli command wiring only; no codec logic.

For the reasoning behind design decisions already made — what was tried, what was rejected and why,
what was measured — see `docs/research.md` before re-deriving or re-proposing something that may
already have been ruled out (e.g. motion compensation, RDO layer, various asset-trimming schemes).
