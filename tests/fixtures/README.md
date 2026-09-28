# Test fixtures

This file is the single source of truth for the test and benchmark videos: which fixtures exist, which
profile each one is run at, and how the derived clips are generated. `docs/feasibility-plan.md` and
`docs/implementation-plan.md` both refer to it and do not repeat it.

Every video is stored through Git LFS (`*.mp4` and `*.gif`, see `.gitattributes`).

## Catalog

Metadata is from ffprobe (2026-09-29).

| ID | File | Resolution | fps | Duration | Frames |
| --- | --- | --- | --- | --- | --- |
| `easy/ball` | `easy/BounchingBall.gif` | 600×450 | 100/9 (11.1) | 0.72 s | 8 |
| `easy/complex` | `easy/Complex.mp4` | 1280×720 | 24 | 10.0 s | 240 |
| `easy/overlap` | `easy/Overlap.mp4` | 384×376 | 30 | 8.6 s | 259 |
| `easy/sequence` | `easy/Sequence.mp4` | 1280×720 | 24 | 10.0 s | 240 |
| `medium/badapple` | `medium/Bad Apple.mp4` | 1440×1080 | 60 | 219.1 s | 13140 |
| `medium/fish-short` | `medium/Don't watch fish spinning anymore.mp4` | 1280×720 | 29.97 | 16.5 s | 491 |
| `medium/fish` | `medium/Fish spinning.mp4` | 1920×1080 | 60 | 178.6 s | 10709 |
| `hard/birdbrain` | `hard/Birdbrain.mp4` | 1920×1080 | 24 | 255.7 s | 6134 |
| `hard/machinelove` | `hard/Machine Love.mp4` | 1920×1080 | 30 | 286.5 s | 8593 |
| `hard/rollback` | `hard/rollback.mp4` | 1920×1080 | 24 | 155.1 s | 3720 |
| `hard/minecraft` | `hard/This is Minecraft.mp4` | 1920×804 | 24000/1001 | 103.5 s | 2481 |

The rename in commit `f82dd56` shows how these relate to the names used in `docs/direction.md` (section
13):

| Old name | Current ID |
| --- | --- |
| `short_animation_720p` | `medium/fish-short` |
| `bad_apple_fhd_60fps` | `medium/badapple` |
| `fish_spinning_fhd` | `medium/fish` |
| `birdbrain_realword_fhd` | `hard/birdbrain` |
| `minecraft_cinamic_fhd` | `hard/minecraft` |

## Main rule: never render information nobody needs

The medium and hard videos are long, high-resolution and high frame rate. Tests and benchmarks **must
not** run on them at native length, resolution and fps unless the user explicitly asks for it. They run
on a **derived clip** instead: a short window, scaled down, with the frame rate capped. That derived clip
becomes the *source* for every method being compared (the v1 baseline, the simulator, the new encoder
and the verifier). A/B comparisons therefore stay fair, and quality is measured against the clip, not
against the original video.

The easy videos are already small, so they run at native settings.

## Profiles

| Profile | Applies to | Window | Height | fps | Used for |
| --- | --- | --- | --- | --- | --- |
| `native` | easy | whole video | native | native | every test and benchmark on easy |
| `quick` | medium, hard | 3 s | 360 (width `-2`, i.e. aspect-preserving and even) | `min(source, 24)` | fixture tests during the development loop |
| `standard` | medium, hard | 10 s | 720 (width `-2`) | `min(source, 30)` | feasibility measurements, milestone acceptance, M0.5 calibration |
| `full` | medium, hard | whole video | native | native | **only when the user explicitly asks**, e.g. checking a performance projection |

A profile never scales **up**. If the source height is at or below the target height, it keeps the
source height. Likewise the fps filter is skipped when the source fps is already at or below the cap.

## Choosing the window (`quick` and `standard`)

1. The candidate window starts at `From = floor(0.25 × duration)` seconds and lasts as long as the profile
   says. If `From + length > duration`, use `From = max(0, duration − length)` instead.
2. Reject the window if either of these holds:
   - it is near-static: at least 90% of consecutive frame pairs have PSNR ≥ 50 dB;
   - it is black: the window's mean luma is below 10.
3. If the window is rejected, move it forward by one window length and check again, up to 3 times. If
   every attempt is rejected, keep the last one and note it in the manifest.
4. Record the chosen window in the table below. Once recorded it is **frozen**: later milestones use the
   same windows so their A/B results remain comparable.

| ID | `quick` From | `standard` From | Note |
| --- | --- | --- | --- |
| *(filled in the first time the clips are generated)* | | | |

## Generating a clip

Run this once per (ID, profile) pair, write the result into the cache, and reuse it afterwards:

```text
ffmpeg -y -ss <From> -i "<source>" -t <length> -an \
  -vf "fps=fps=<fpsOut>:round=<R>,scale=-2:<H>:flags=area" \
  -c:v libx264 -preset slow -crf 10 -pix_fmt yuv420p "<cache>/<profile>/<tier>-<name>.mp4"
```

- Put `fps` before `scale`, so dropped frames are never scaled. Drop whichever filter is not needed
  (see the no-upscale and no-cap rules above).
- `R` is the `round` value chosen by the feasibility work V10b, which gives the "a dropped frame is
  represented by the frame before it" semantics. Until V10b has run, use `near` and write that into the
  manifest.
- `flags=area` avoids aliasing when downscaling.
- CRF 10 is effectively transparent. It does not need to be mathematically lossless, because every method
  reads the same clip.
- Cache: `%TEMP%\osbmpeg-clips\`. Next to each clip write `<clip>.json`, recording the source path, the
  source size and mtime, `From`, the length, the output W×H, the output fps, `R`, and the ffmpeg version.
  Regenerate a clip whenever any of these no longer matches.
- `easy/ball` is a GIF. If a tool cannot read it directly, normalize it once losslessly with
  `-c:v libx264 -crf 0 -pix_fmt yuv444p`.

## Prerequisites

`ffmpeg` and `ffprobe` must be on `PATH`. On the machine these fixtures were probed on, the only copy was
the one bundled with Krita (`C:\Program Files\Krita (x64)\bin`), and it was not on `PATH`. The plans
support the environment variable `OSBMPEG_FFMPEG_DIR` to point at that folder.
