# OsbMpeg v2 — Closed-Loop Storyboard Encoder (Technical Plan)

> Trạng thái: **draft chờ review**. Đầu vào: concept document (Video → osu! Storyboard, fidelity trước
> compression), điều tra codebase hiện tại, và báo cáo research osu!wiki + ppy/osu + ppy/osu-framework
> (facts được đánh dấu `[R#]` bên dưới, trích từ báo cáo đó). Draft đã qua một vòng adversarial
> review nội bộ (15 findings — 1 blocker về baker×motion property collision, 12 major/minor — tất cả
> đã hấp thụ vào bản này: single-writer rule + `PropertyOverlapValidator`, RDP screen-space joint,
> hysteresis bất đối xứng, crossfade span cap, M0.5 calibration spike, M0 positive control,
> shared-decode N=1, transient overdraw budget, 4K guard, verify luôn ghi artifact).
> Plan này là spec triển khai cho implementer (Sonnet); mọi con số đánh dấu *(provisional)* sẽ được
> calibrate ở milestone tương ứng, không phải hằng số bất di bất dịch.
>
> **Bức tranh dài hạn**: `docs/research-representations.md` — representation taxonomy, so sánh
> decomposition strategies, optimization strategy, cost model, failure modes, decision matrix,
> roadmap end-state. Plan này là chặng M0→M3 của roadmap đó; sau M3 là architecture checkpoint
> (M3.5) với attribution report quyết định hướng kế bằng số liệu.
>
> **Spec triển khai chi tiết (Sonnet đọc để code): `docs/blueprint-v2.md`** — module map,
> interfaces, thuật toán per step, tests/acceptance per milestone, architectural invariants I1-I10,
> open questions OQ-1..6. Blueprint thắng plan này khi mâu thuẫn về chi tiết implementation.

---

## 0. TL;DR

v1 mờ vì **quality floor tương đối** (baseline − 1dB, tuner chủ động ép chất lượng xuống sát floor),
**palette quantize không dither**, và **open-loop staleness** (quantized hash + tolerance đóng băng
tile đang đổi chậm, không gì đo error trên frame thực tế ship ra). v1 nặng vì **chỉ có một primitive**:
crop tĩnh + 1 lệnh `S` — mọi chuyển động ⇒ re-emit cả vùng mỗi frame (Minecraft full clip: 79,612
sprites / 744MB).

v2 đổi hai thứ:

1. **Closed-loop encode**: encoder tự mô phỏng đúng cái osu! sẽ render (reconstruction state), đo
   error per-frame per-block so với source, và chỉ chi primitive ở chỗ error vượt **floor tuyệt đối**
   do user chọn (`--quality`). Mờ trở thành *không thể xảy ra by construction* — frame nào dưới floor
   thì encoder buộc phải chi thêm, worst case degenerate về full-frame refresh (vẫn đúng, chỉ tốn).
2. **Palette primitive khớp năng lực thật của storyboard**: static patch, **crossfade** (2 sprite +
   `F`, nội suy tuyến tính thay cho đóng băng), **motion layer** (1 sprite nền + `M`/`S` commands cho
   camera pan/zoom — commands nội suy liên tục tại refresh rate thực nên mượt hơn cả source fps
   `[R1]`), Animation fallback cho vùng thrash, và budget renderer workload đo bằng mô hình cost
   thật của lazer (fill-rate + alive sprites + resident texture bytes, không phải tổng command).

`ParameterTuner` 4 chiều chết. `TileSize`/`HashQuantLevels`/`TileTolerance`/`Colors` không còn là knob
correctness. Thay bằng `--quality high|medium|low`.

---

## 1. Mục tiêu & tiêu chí nghiệm thu

Ba mục tiêu của concept doc, chuyển thành metric đo được:

| Mục tiêu | Metric | Cách đo |
|---|---|---|
| Giữ chất lượng | **Per-frame floor tuyệt đối**: 100% frames đạt PSNR ≥ P và worst-tile SSIM ≥ S (theo preset, §7) | `verify` pass: render toàn bộ output bằng software renderer tại source resolution, so từng frame với source |
| Gọn nhẹ | Asset bytes, `.osb` bytes, asset count | `WorkloadAnalyzer` report |
| Không quá tải renderer | Peak **SB load** (tổng diện tích alive-sprite / viewport 640×480 `[R3]`), peak alive sprites, **Animation resident bytes** (mọi frame của Animation nằm RAM đồng thời `[R4]`), `.osb` line count (load time) | `WorkloadAnalyzer` sweep timeline |

Nguyên tắc xung đột: **fidelity thắng**. Khi budget workload bị vượt, encoder đổi *representation*
(crossfade dài hơn, merge patch, JPEG, keyframe thưa hơn) chứ không bao giờ âm thầm hạ floor; nếu vẫn
vượt → in violation report + gợi ý hạ preset, user quyết.

---

## 2. Chẩn đoán v1 (root causes, có citation)

### 2.1 Mờ

1. **Floor tương đối** — `ParameterTuner.cs:55` `TargetSlackDb = 1.0`; floor = PSNR của combo mặc định
   trừ 1dB. Content khó (bad_apple baseline ~24.9dB) → floor ~23.9dB: ảnh nát vẫn "đạt". Tuner
   minimize bytes *xuống sát floor* — chất lượng thấp là mục tiêu tối ưu, không phải tai nạn.
2. **Octree quantize không dither** — `AssetStore.cs:232` `OctreeQuantizer(MaxColors=Colors)`;
   tuner từng chọn `Colors=16` cho short_animation → posterization/banding trên gradient.
3. **Open-loop staleness** — `TileRunTracker` chỉ đóng run khi *quantized* hash đổi (+ `TileTolerance`
   MAE budget). Thay đổi chậm (dissolve, gradient, cảnh tối) trôi dưới ngưỡng vô hạn → tile đứng
   hình cũ (smear/ghost). Không có gì đo error trên frame ship ra — PSNR chỉ đo lúc tuning, trung
   bình trên sample window ngắn, nên staleness cục bộ/tạm thời không bị bắt (`Metrics.cs` SSIM cũng
   là global-frame, không windowed).
4. **PSNR-mean là metric duy nhất trong tuner** — mù với degradation cục bộ.

### 2.2 Nặng

1. **Một primitive duy nhất** — `TileEncodeLoop.Emit`: mỗi run = 1 sprite + 1 lệnh `S` tĩnh (scale
   placement). Không dùng `M`/`F`/`C`/`R` động nào cho nội dung. Camera pan ⇒ mọi tile đổi mỗi frame
   ⇒ re-emit toàn canvas tại source res. Minecraft full clip 60fps: **79,612 sprites / 79,223 assets
   / 735MB assets + 9.31MB .osb** (docs/research.md, 2026-08-22).
2. **Animation không dedupe** được (format cần file đánh số riêng), tới 300 frame full-res PNG mỗi
   object — và mọi frame resident RAM đồng thời khi load `[R4]`.
3. 60fps frame-duplication nhân đôi mật độ run boundary.

### 2.3 Cái v1 làm đúng (giữ)

- Decode ở **source resolution**, mapping 640-space chuẩn theo lazer (`CanvasMapping` khớp
  DrawableStoryboard math) — research xác nhận đây đúng là chiến lược sharp duy nhất `[R2]`. Mờ của
  v1 **không** đến từ resolution.
- Parsers layer hoàn chỉnh: IR, `OsbWriter` (float, shorthand, loop), `CommandEvaluator` +
  `EasingTable` (đủ easing 0..34), passes (merge/drop/loop-extract).
- `Compositor` blend semantics đã verify khớp osu-framework (normal = straight alpha lerp, additive).
- Scene detection (`ScenePrePass`/`SceneBounds`) — signal combo-independent, unit-tested.
- `AssetStore` content-addressed XXH3-128, in-memory mode, cross-scene dedupe.
- `FrameSource` streaming decode + hwaccel, `MediaProbe`, `Metrics.Psnr`.

---

## 3. Facts từ research quyết định thiết kế `[R#]`

Trích từ báo cáo research (osu!wiki + DeepWiki ppy/osu, ppy/osu-framework). Encoder PHẢI tôn trọng:

- **[R1] Commands nội suy liên tục.** Mỗi command thành osu!framework `Transform`, evaluate như hàm
  liên tục của thời gian mỗi `Update()` tại refresh rate thực (60/144/240Hz). Motion qua `M`/`S`
  KHÔNG cần 1 command/frame — vài keyframe linear easing là mượt tuyệt đối.
- **[R2] Scale float tự do, bilinear + mipmap (≤3 levels).** Emit asset tại source resolution, scale
  xuống 640-space — sharp tại mọi màn hình. Storyboard-space → screen scale = `screen_height/480`
  (2.25× @1080p, 3× @1440p, 4.5× @2160p).
- **[R3] Cost model renderer**: per-frame cost ≈ fill-rate/overdraw (diện tích alive sprites — texel
  trong suốt trong quad opaque-alpha VẪN tốn) × texture binds; sprite **ngoài lifetime** cost ~0
  (skip cả Update lẫn Draw); alpha=0 mid-lifetime vẫn alive (vẫn walk transforms). Tổng
  sprite/command toàn timeline chủ yếu là cost parse-time/memory. Thiết kế time-disjoint
  one-sprite-per-region đã tối ưu draw cost — v1 đúng ở điểm này; vấn đề của v1 là bytes/assets,
  không phải draw model.
- **[R4] Animation frames preload toàn bộ, không stream** — N frames = N textures resident đồng
  thời. Frame count của Animation là trục đắt thật sự về memory.
- **[R5] Overlapping same-property commands: stable vs lazer resolve KHÁC NHAU, vĩnh viễn**
  (ppy/osu#7257). Mọi track per-property phải disjoint (`next.start == prev.end`). Property thật sự:
  X, Y, ScaleX, ScaleY, Rotation, Alpha, Colour, Blending, Flips — `S` và `V` đụng nhau trên Scale,
  `M` đụng `MX`/`MY`.
- **[R6] Atlas 1024×1024** — asset ≤1024² (trừ padding) được batch chung atlas; lớn hơn vẫn render
  (tới MaxTextureSize ~4096) nhưng mất batching. Ranking criteria: ≤17Mpx²/ảnh.
- **[R7] Lifetime**: `StoryboardSprite.StartTime` tự derive từ điểm alpha>0 đầu tiên (leading
  zero-alpha free), KHÔNG có trailing tương đương — encoder phải kết thúc mọi sprite bằng
  command chấm dứt đúng lúc (command cuối kết thúc tại đúng thời điểm sprite hết nhiệm vụ).
- **[R8] Widescreen là key `WidescreenStoryboard: 1` trong `.osu`, không phải `.osb`** — packaging
  requirement, compiler không tự bật được. Document trong README/output note.
- **[R9] Không bao giờ emit `[Variables]`** (case thật: 554 variables → load 3s thành 45s). Ưu tiên
  value-chaining shorthand (1 dòng command nhiều cặp giá trị tự expand chuỗi bước đều) cho chain
  keyframe bước cố định.
- **[R10] Prior art scale**: storyboard 34,847 sprites / 977k lines / 27MB `.osb` load ~3s và chạy
  được — không có sprite-count wall; degrade mềm. `.osb` >10MB bắt đầu có drawback (osb.moe).

---

## 4. Kiến trúc v2

### 4.1 Nguyên lý

```text
                     source frames (source res, source fps)
                                    │
              ┌─────────────────────┼──────────────────────┐
              │  per scene          ▼                      │
              │        GlobalMotionEstimator               │
              │   (camera translate+zoom per frame,        │
              │    accept/reject per scene)                │
              │                     │                      │
              ▼                     ▼                      │
        ClosedLoopEncoder ◄── ReconstructionState          │
        (error-driven)        (mô phỏng chính xác          │
              │                cái osu! sẽ hiển thị)       │
              │ emit primitives:                           │
              │   MotionLayer / StaticPatch /              │
              │   Crossfade / Animation                    │
              ▼                                            │
        SbDocument IR ──► IR passes ──► verify pass ───────┘
              │                          (per-frame floor, bắt buộc)
              ▼
        .osb + assets + WorkloadReport
```

Vòng lặp cốt lõi: **emit → mô phỏng → đo error → sửa chỗ sai → lặp**. Mọi cơ chế "đoán" (hash
quantization, tolerance) bị loại khỏi đường quyết định correctness; hash chỉ còn dùng cho asset
dedupe (việc nó làm tốt).

### 4.2 Vì sao không phạm "Rejected directions" trong docs/research.md

- **`GlobalMotionEstimator` từng bị reject** — lý do reject gắn chặt với kiến trúc fixed-grid
  conditional replenishment: motion vector không đổi được việc asset vẫn crop từ grid cố định của
  frame hiện tại, và shifting sampling grid mở coverage gap. v2 đổi representation: asset là **sprite
  persistent di chuyển bằng `M`/`S` command** — không re-crop mỗi frame, motion vector trực tiếp
  thành command. Coverage gap ở mép pan là *có chủ đích* và được closed-loop patch xử lý như mọi
  vùng dirty khác. Tiền đề của rejection cũ không còn tồn tại.
- **RDO framework từng "not built vì undemonstrated"** — v2 không xây RDO candidate-search 4D. Vòng
  closed-loop là error-driven refresh (một chiều: error vượt floor → chi primitive rẻ nhất đạt
  floor), không phải cost-model search. Đơn giản hơn về kind.
- **`AssetTrimmer` alpha-masking bug** (transparency lộ canvas trống vì sprite cũ đã chết) — không
  đụng: crossfade của v2 giữ sprite cũ **alive tới khi sprite mới opaque hoàn toàn** (§5.2), đúng
  bài học đó.

### 4.3 Reuse map

| Giữ nguyên | Sửa/mở rộng | Bỏ |
|---|---|---|
| `OsbMpeg.Parsers` toàn bộ | `OsbWriter`: value-chaining shorthand `[R9]` (M3) | `ParameterTuner` (toàn bộ) |
| `FrameSource`/`MediaProbe`/`VideoFrame` | `Compositor`: bilinear sampling (M0) | `TileEncodeLoop` (thay bằng `ClosedLoopEncoder`) |
| `ScenePrePass`/`SceneBounds` (+`TileRunTracker` chỉ còn phục vụ detection) | `Metrics`: windowed SSIM (M0) | `HashQuantLevels`/`TileTolerance`/`Colors` như correctness knobs |
| `AssetStore` (content-addressed + in-memory) | `AssetStore`: JPEG option + dithered palette, đều gated SSIM (M3) | `QuadtreeMerger` (thay bằng dirty-rect coalescing; logic tham khảo được) |
| `CommandEvaluator`/`EasingTable`/`SoftwareStoryboardRenderer` | `SoftwareStoryboardRenderer`: dùng làm verify pass renderer | Heatmap partitioning spike (đã reject) |
| `GroupTransformBaker`, `.osbv` semantics, CLI shape | `VideoCompiler`: orchestrate pipeline mới | `AnimationDetector` (logic tái dùng trong ThrashPlanner M3) |

---

## 5. Primitives

Mọi primitive emit ra IR hiện có (`SbSprite`/`SbAnimation` + commands) — không cần format mới.
Ràng buộc chung:

- **Per-property command tracks disjoint `[R5]`**, enforce bằng máy chứ không bằng kỷ luật:
  1. **Single-writer rule**: mỗi property (X, Y, ScaleX, ScaleY, Rotation, Alpha, Colour, Blending,
     Flips) của một object có đúng MỘT nguồn ghi. Khi `GroupTransformBaker` sở hữu một object (mọi
     `AnimationVideo` có commands — trường hợp thường gặp, không phải edge case), baker là single
     writer của position/scale/alpha: **camera motion của MotionLayer PHẢI fold vào tham số base của
     baker** (`baseCenterX/Y`, `baseScale` biến thiên theo t, đưa vào trước phép compose trong
     `SampleAt`) chứ không bao giờ emit track `MX`/`MY`/`S` riêng cạnh track baker — hai phép biến
     đổi không giao hoán, phải compose trong một chỗ.
  2. **`PropertyOverlapValidator`** (IR pass mới, chạy trong M0, gate mọi output): assert mọi cặp
     command cùng property trên cùng object không chồng thời gian → compile fail nếu vi phạm. Bắt
     buộc vì không tầng nào hiện có bắt được lỗi này: `MergeAdjacentCommands` chỉ check adjacency,
     `OsbValidator` chỉ đếm object, và `CommandEvaluator` resolve overlap kiểu "last-in-list wins" —
     một cách thứ ba khác cả stable lẫn lazer, nên verify pass sẽ pass sạch trong khi client thật
     render khác đi.
- Asset đơn ≤1024×1024 khi có thể `[R6]` (ngoại lệ: base sprite của motion layer, chấp nhận mất
  atlas batching vì chỉ 1-2 cái alive). **Nguồn có chiều nào >3840px → scene đó skip MotionLayer**
  (fallback P1/P2 như motion bị reject) — base sprite source-res sẽ chạm `MaxTextureSize` (~4096,
  GPU-dependent); area cap 17Mpx² không bắt được vi phạm per-dimension.
- Sprite kết thúc đúng thời điểm hết nhiệm vụ `[R7]`; z-order = thứ tự declare trong layer → emit
  theo thứ tự thời gian bắt đầu, sprite đè lên vùng cũ luôn declare sau.

### 5.1 P1 — StaticPatch

Như v1: crop content tại rect, hiển thị `[t0, t1)`. 1 sprite, `S` scale placement (giữ đúng cách
hiện tại). Dedupe qua `AssetStore`. Dùng cho: nội dung tĩnh trong khoảng thời gian.

### 5.2 P2 — Crossfade (vũ khí chống staleness)

Thay thế "hard swap mỗi khi hash đổi" cho **thay đổi chậm/mượt** (dissolve, gradient dịch, ánh sáng
đổi):

```text
sprite A (nội dung tại t_k)   : alive [t_k, t_{k+1}]            ← giữ alive suốt fade
sprite B (nội dung tại t_{k+1}): F,0,t_k,t_{k+1},0,1  (trên A)  ← declare sau A
```

Renderer blend normal = `lerp(dưới, trên, alpha)` (Compositor đã verify khớp shader lazer) ⇒ các
frame trung gian ≈ nội suy tuyến tính giữa 2 keyframe thật. Encoder **verify từng frame trung gian —
đầy đủ, không sample**: crossfade span bị cap ≤ sliding window (§6.4) để mọi frame trung gian còn
trong memory lúc quyết định; quyết định định floor không bao giờ dựa trên sample. Không đạt floor →
chèn keyframe giữa (chia đôi span) hoặc hard swap. Overdraw: +1 lớp trên vùng đó trong fade window —
tính vào **budget transient riêng** (§7), không tính vào steady-state SB cap: một global dissolve
(đúng ca P2 sinh ra để phục vụ) đưa cả frame vào 2 lớp cùng lúc là hành vi đúng, không được để
budget ép nó quay về hard swap. Mỗi vùng tối đa 1 crossfade active; cần đổi tiếp khi đang fade →
hard swap (nội dung nhanh, crossfade sai công cụ).

Win kép: (a) fidelity — thay đổi chậm được *nội suy trung thực* thay vì đóng băng; (b) bytes —
refresh rate của vùng đổi chậm giảm mạnh (1 cặp keyframe thay cho hàng chục patch).

### 5.3 P3 — MotionLayer (camera pan/zoom)

Per scene, nếu global motion model được chấp nhận (§6.3):

- **Base sprite**: full-frame crop tại keyframe `t_k`, origin `Centre`, source resolution.
- **Tracks**: keyframe segments linear easing cho `p(t)` (position of centre) + `s(t)` (scale), giải
  từ similarity transform per frame — toán: điểm màn hình `= p(t) + s(t)·(x − c)` với `c` = tâm ảnh.
  (Với `p`, `s` đều linear per-segment, chuyển động màn hình của mọi điểm ảnh cố định cũng linear
  per-segment — composition đóng.) Object có baker: track này fold vào baker (single-writer rule §5);
  object không baker: emit `MX`/`MY`/`S` trực tiếp.
- **Keyframe reduction: RDP joint trên transform, error đo ở screen space** — distance function =
  **max corner displacement** của composed similarity (4 góc ảnh), epsilon 0.3 osu!px
  *(provisional)*. KHÔNG chạy RDP riêng từng thành phần với epsilon position-unit: sai số scale Δs
  tốn `Δs·|x−c|` px — lớn nhất ở góc ảnh, gần như vô hình với epsilon đo trên (tx,ty). Simplify
  joint cũng giữ breakpoints của `MX`/`MY`/`S` đồng bộ — điều kiện để value-chaining shorthand
  `[R9]` dùng được ở M3.
- Mép lộ ra khi pan (coverage gap chủ đích) + foreground chuyển động khác camera → dirty blocks →
  P1/P2 phủ lên trên.
- **Keyframe refresh**: khi residual toàn cục leo (drift tích lũy, occlusion) → base keyframe mới,
  chuyển bằng crossfade hoặc cut tại điểm error spike.
- Commands nội suy liên tục `[R1]` ⇒ pan mượt tại 144Hz dù source 24fps — *mượt hơn source*.

Không làm ở v2.0: mosaic stitching (ghép các frame dọc pan thành 1 asset lớn phủ cả quãng) — ghi là
optimization tương lai, gated sau M2 nếu đo thấy revealed-edge patches chiếm tỷ trọng lớn.

### 5.4 P4 — Animation (fallback cho thrash)

Vùng dirty gần như mọi frame mà crossfade fail (noise, particle, video nhiễu): `SbAnimation` như v1
nhưng bị budget chặn: frame count cap *(provisional: 120)*, region area cap, tổng **resident bytes**
(`Σ frames × w × h × 4`) per scene cap `[R4]`. Vượt cap → chấp nhận patch-rate cao bằng sprites
(sprites còn dedupe được, animation thì không).

### 5.5 P5 — Photometric commands (rẻ nhất, làm trước)

Trước khi chi patch: thử giải thích thay đổi toàn vùng bằng command trên sprite **đang có**:

- Fade to/from black, flash trắng: `F` hoặc `C` (multiplicative tint `[R3]`) trên các sprite hiện có
  — hoặc đơn giản hơn: 1 sprite đen/trắng phủ toàn màn với `F` (1 asset 1×1... **không** — texel
  1×1 scale to màn = fill-rate full screen 1 lớp, chấp nhận được, nhưng dùng ảnh đơn sắc nhỏ ~16×16).
- Chỉ nhận khi verify đạt floor (như mọi primitive). Bắt được fade in/out — pattern cực phổ biến,
  gần như miễn phí về bytes.

---

## 6. ClosedLoopEncoder — thuật toán (per scene)

### 6.1 State

- `ReconstructionState R`: canvas RGB source-res, mô phỏng chính xác composite osu! tại thời điểm t
  (dùng `Compositor` bilinear + `CommandEvaluator` — cùng code với verify pass). Cập nhật
  **incremental**: static patch blit 1 lần khi mở; vùng có primitive động (crossfade đang chạy,
  motion layer) re-composite mỗi frame chỉ trong bbox của nó.
- `active[region]`: primitive đang phủ mỗi vùng (để biết crossfade từ cái gì, đóng cái gì).

### 6.2 Vòng chính

```text
frame 0 của mỗi scene: full-frame refresh bắt buộc (I-frame) — R trống, không có gì để "chờ"
for each source frame F_t (source fps, KHÔNG duplication):
    R.AdvanceTo(t)                          # composite incremental các primitive động
    E = ErrorMap(R, F_t)                    # per 16×16 block: luma SSD + tile-SSIM 64×64
    dirty  = blocks dưới TRUE floor (preset.P)          → hành động NGAY frame này, không debounce
    early  = blocks dưới preset.P + 2dB headroom        → hysteresis 2 frame (chống flap) rồi mới dirty
    # hysteresis bất đối xứng: chỉ áp cho headroom trigger, không bao giờ cho true floor —
    # floor tuyệt đối nghĩa là không frame nào được ship dưới nó, kể cả trong debounce window.
    # Patch emit sau debounce: StartMs lùi về frame vi phạm đầu tiên (frame còn trong sliding window).
    if dirty empty: continue
    rects = CoalesceDirtyRects(dirty)       # greedy merge blocks kề nhau, cap 1024² mỗi rect
    for each rect:
        1. thử P5 (photometric trên primitive đang có)     — verify floor
        2. thử P2 crossfade từ nội dung đang hiển thị      — verify mọi frame trung gian
        3. else P1 hard swap (đóng patch cũ tại t, mở patch mới crop F_t)
    R.Apply(emitted)                        # blit ngay, giữ R = ground truth của output
    assert ErrorMap(R, F_t) đạt floor       # guaranteed: bước 3 luôn thỏa
post-scene: ThrashPlanner quét patch-rate per region → nâng cấp P4 nơi đáng (M3)
```

Ghi chú then chốt:

- **Floor tuyệt đối per-block, không trung bình** — mờ cục bộ không thể trốn sau mean.
- Bước 2 (crossfade) hoạt động *hồi tố*: khi block dirty tại t nhưng lần refresh trước tại t_prev,
  encoder kiểm tra chuỗi frame `(t_prev, t)` xem `lerp(content_prev, content_t, α)` có đạt floor
  không (cần giữ các frame trong khoảng — xem 6.4 memory). Đạt → 1 crossfade thay vì swap; không →
  bisect khoảng.
- Deterministic, single-pass chính + verify pass cuối. Không search 4D, không probe 40 lần.

### 6.3 GlobalMotionEstimator (chạy trước vòng chính, per scene)

- Downscale luma ≤256px wide. Block matching pyramidal (3 levels) → per-block vectors → RANSAC fit
  similarity `(tx, ty, s)` (bỏ rotation ở v2.0 — thêm sau nếu corpus cần, `R` command sẵn chờ).
- **Accept = 2 gate**, cả hai phải đạt:
  1. ≥60% *(provisional)* blocks inlier (residual < 1px tại full res sau warp) trong ≥70% frames.
  2. **Post-fit residual gate**: tổng diện tích dirty-block dự kiến (phần camera model KHÔNG giải
     thích được) ≤ 25% *(provisional)* diện tích frame, trung bình trên scene — inlier ratio một
     mình cho phép "accept" một layer mà 40% frame vẫn phải patch mỗi frame *chồng lên* một base
     sprite full-res cũng đang trả tiền; gate 2 đo thẳng cái quyết định net win.
  Reject bất kỳ gate nào → scene chạy thuần P1/P2 (an toàn tuyệt đối: motion layer chỉ là
  optimization, closed loop sửa mọi sai số của nó bằng patches).
- Tự viết, không thêm dependency CV. **M2 mở đầu bằng spike đo trước khi build đủ** (cùng kỷ luật
  research.md áp cho RDO): implement naive translation-only trên `pan_synthetic` + 1 fixture thật,
  đo inlier/residual, rồi mới quyết pyramidal/RANSAC đầy đủ hay phase correlation. Không coi ước
  lượng LOC là schedule input.

### 6.4 Chi phí encode & memory

- Không còn tuner (40 probes) — một pass phân tích + một pass verify. Kỳ vọng nhanh hơn v1 tuning
  đáng kể (v1: ~100-170s tuning/scene chưa kể encode). **Cảnh báo từ lịch sử đo của chính repo**:
  render+PSNR từng là cost trội khó giảm (docs/research.md, Global auto-tune) — v2 render mọi frame
  full-res 2 lần (loop + verify). Target *(provisional)* **≤4× realtime 1080p30 8-core, ĐÃ GỒM
  verify pass**; đo thật trên fish_spinning ngay trong M1 trước khi coi là target thay vì guess.
  Verify pass cost scale theo overdraw/alive sprites, không theo frame count — tệ nhất đúng trên
  fixtures patch-rate cao (bad_apple).
- Crossfade cần lookback frames giữa 2 keyframe: giữ **sliding window** decoded frames (cap
  *(provisional)* 2s tại source fps, ~370MB @1080p30 — cùng cỡ shared-decode hiện tại đã chấp nhận
  trong docs/research.md). **Crossfade span cap = window size** (§5.2) — không có nhánh sampled
  verification; quyết định định floor luôn dựa trên đủ frame.
- Verify pass cuối: render mọi frame output vs source, **luôn tại source-fps timestamps** bất kể
  requested-fps flag (so sánh 1:1 với ground truth thật, không so với frame duplicate). Bắt buộc,
  in report. Vi phạm floor → **vẫn ghi output + violation report đầy đủ, exit code ≠ 0** — không
  vứt artifact của một compile có thể 9h (Minecraft full: 542 phút, docs/research.md); caller quyết
  ship-flagged hay re-run.
- **Shared-decode multi-target scope v2.0 = N=1 — implementation constraint, không phải
  architectural principle** (Q6): closed loop có per-target state (mỗi target một
  `ReconstructionState` + sliding window riêng — mapping/baker khác nhau ⇒ error map khác nhau).
  Kiến trúc từ M1: decode (`FrameSource`) tách khỏi consumer qua interface phân phối frame cho N
  reconstruction states; v2.0 chỉ wire N=1 (multi-member plan fallback per-member decode, regression
  có chủ đích, document trong CLAUDE.md). Nâng N>1 = wire lại + price memory N×window, không đổi
  kiến trúc.

---

## 7. Quality presets & CLI

```text
compile <input.osbv> <output.osb> <assets-dir> [--hwaccel MODE] [--quality high|medium|low]
```

Gate = **per-frame PSNR min** + **worst-tile SSIM** (64×64 luma). Global SSIM chỉ in trong report,
KHÔNG gate — nó là aggregate kiểu mean, đúng loại mù cục bộ mà §2.1 kết án; một tile hỏng trong
frame đẹp gần như không dịch chuyển nó (`Metrics.cs` tự ghi chú điều này).

| Preset | Per-frame PSNR min | Worst-tile SSIM |
|---|---|---|
| `high` (default) | 38 dB | 0.92 |
| `medium` | 34 dB | 0.88 |
| `low` | 30 dB | 0.80 |

**Số trong bảng là placeholder, ĐƯỢC CHỐT bởi M0.5 calibration spike (§9) trước khi M1 bắt đầu** —
không để M1 acceptance gate trên hằng số mà chính M1 mới calibrate (vòng lặp). Dữ liệu lịch sử cảnh
báo: bad_apple baseline ~24.9dB *mean*; per-frame *min* 34dB trên content nhiễu có thể đòi patch rate
sát NaiveBaseline — spike đo curve floor↔patch-rate per fixture và đặt số (kể cả khả năng preset
per-frame-min thấp hơn đáng kể so với mean-target trực giác).

Workload budgets (defaults, `--max-*` flags override, chỉ enforce từ M3):

- **Steady-state** SB load ≤ 4.0 viewport; peak alive sprites ≤ 500
- **Transient** (fade-window overdraw của crossfade): budget riêng ≤ +2.0 viewport *(provisional)*,
  không tính vào steady-state cap (§5.2)
- Animation resident ≤ 200MB/scene; asset đơn ≤ 1024² (motion-layer base exempt)
- `.osb` ≤ 10MB/phút video *(soft, warn)* `[R10]`

Output cuối luôn in: quality report (min/p1/mean PSNR & SSIM per scene) + workload report. Ghi chú
`WidescreenStoryboard: 1` requirement vào output note `[R8]`.

---

## 8. Validation & benchmark protocol

- **Corpus**: 5 fixtures hiện có (short_animation, birdbrain, bad_apple, fish_spinning, minecraft) +
  3 synthetic mới: `pan_synthetic` (ảnh tĩnh lớn pan đều — nghiệm thu M2), `dissolve_synthetic`
  (gradient crossfade chậm — nghiệm thu P2), `pan_foreground_synthetic` (pan + foreground độc lập,
  marginal case cho motion accept gate — nghiệm thu M2).
- **A/B bảng chuẩn** mỗi milestone: asset bytes, `.osb` bytes, sprites, commands, peak SB load, min
  frame PSNR / worst-tile SSIM, encode wall time. So với v1 output trên cùng fixture/window.
- **Golden tests**: SoftwareStoryboardRenderer render các `.osb` fixture tay (có M/S/R/F/C/easing)
  so pixel với expected — chốt parity renderer trước khi tin closed loop (M0).
- Spot-check thủ công trong osu! thật (stable + lazer) mỗi milestone — software renderer là proxy,
  divergence phải bắt sớm.

---

## 9. Milestones

### M0 — Verification harness (mọi thứ sau gate trên cái này)

1. `Compositor`: bilinear sampling (giữ nearest cho test cũ); golden tests vs expected renders.
2. `Metrics`: windowed/tile SSIM (64×64 luma, trả min + p1 + mean).
3. `WorkloadAnalyzer` (mới, `Compiler/Evaluation/`): sweep timeline SbDocument → peak SB load
   (steady + transient tách riêng), peak alive sprites, animation resident bytes, asset bytes,
   line count.
4. `PropertyOverlapValidator` IR pass (§5) — chạy trên mọi output từ đây trở đi.
5. `verify` hidden subcommand: render output `.osb` + assets vs source video, per-frame report.

**Acceptance** (cần cả negative LẪN positive control):
- Negative: `verify` trên output v1 của bad_apple/fish → report chỉ ra frames dưới floor.
- Positive: một `.osb` synthetic pixel-exact by construction (1 sprite full-frame crop thẳng từ
  source) phải report 100% pass tại `high` — không có case bắt buộc pass thì harness hỏng kiểu
  "fail everything" vẫn lọt gate.
- Golden tests pass.

### M0.5 — Preset calibration spike (nhỏ, trước M1)

Dùng harness M0 + encoder mô phỏng thô (hard-swap mọi block dưới floor): đo curve **floor ↔ patch
rate ↔ bytes** trên 5 fixtures. Output: 6 con số preset (§7) chốt bằng dữ liệu + visual spot-check,
commit vào plan. Nửa ngày công, gỡ vòng lặp "M1 gate trên số M1 tự calibrate."

### M1 — Closed-loop encoder: P1 + P2 + P5 (hết mờ)

`ClosedLoopEncoder` + `ReconstructionState` + `ErrorMap` + `DirtyRects` + `CrossfadePlanner` thay
`TileEncodeLoop`; xóa `ParameterTuner`; wire vào `VideoCompiler` (giữ scene loop và
`GroupTransformBaker`; shared-decode multi-target scope N=1 — §6.4).

**Acceptance**: 100% frames đạt floor `medium` (số đã chốt ở M0.5) trên cả 5 fixtures (verify pass);
`dissolve_synthetic` dùng ≤ 1/5 số patch của hard-swap; encode time ≤ v1 (tuning+encode) và đo wall
time thật trên fish_spinning làm baseline perf (§6.4); bytes báo cáo (chưa cam kết — fidelity thật
có thể đắt hơn fidelity giả trên noisy content, đó là cái giá đúng).

### M2 — MotionLayer (hết nặng trên pan/zoom)

Mở đầu bằng **estimation spike** (§6.3), rồi `GlobalMotionEstimator` + `TrajectoryFitter` (RDP joint
screen-space, §5.3) + tích hợp base-layer vào closed loop (fold qua baker khi object có baker — §5).
**Build dưới interface layer-set**: K=1 là case duy nhất v2.0 wire, nhưng estimator/emitter nhận
"danh sách motion layers" — LayeredMotion K>1 (nếu M3.5 chọn) là mở rộng, không phải viết lại
(research-representations.md §2.1). Motion-segment ranh giới thời gian trong scene (Q5) cũng thuộc
milestone này.

**Acceptance**: `pan_synthetic` asset bytes giảm ≥5× vs M1; minecraft 10s window tổng bytes giảm
≥40% vs M1; fixture adversarial mới `pan_foreground_synthetic` (camera pan + chủ thể chuyển động
độc lập, thiết kế để chỉ vừa lọt gate 60/70) chứng minh post-fit residual gate hoạt động (accept
chỉ khi net win thật); fidelity không regression (100% frames vẫn đạt floor); scene bị reject
motion → output identical M1 (fallback sạch).

### M3 — Budgets + ThrashPlanner + byte optimizations

Budget enforcement (§7) + `ThrashPlanner` (P4 với caps) + `AssetStore`: JPEG per-asset (opaque
content, quality search dưới SSIM gate; **verify osu! load .jpg trong storyboard trước khi bật
default**) + PNG palette **có Floyd–Steinberg dithering** gated SSIM + `OsbWriter` value-chaining
shorthand `[R9]`.

**Acceptance**: bad_apple (thrash-heavy) đạt floor với animation resident trong budget; JPEG path
giảm ≥30% asset bytes trên fixture photographic (birdbrain/fish) không thủng SSIM gate; không
fixture nào vượt workload budget mặc định hoặc có violation report rõ ràng.

### M3.5 — Architecture checkpoint (thay M4 cố định)

Không mặc định hướng kế tiếp. Chạy **attribution report** trên toàn corpus (spec đầy đủ:
`docs/research-representations.md` §6.3): phân loại mọi byte asset còn lại vào buckets
`local-coherent-motion` / `pan-reveal` / `perspective-residual` / `near-duplicate-assets` /
`deformation` / `noise`. Bucket lớn nhất ≥ ~25-30% *(provisional)* → build hướng tương ứng trong
menu: **LayeredMotion (K>1)** / **Panorama** / **CanonicalAssetReuse** / **PerspectiveStrips**.
`noise` trội → dừng, ship, ghi giới hạn vào docs. Mỗi hướng trong menu đã có phân tích khả thi +
điều kiện nhận trong research-representations.md — checkpoint chỉ chọn, không thiết kế lại từ đầu.

---

## 10. Risks

| Risk | Mitigation |
|---|---|
| Software renderer ≠ GPU osu! (bilinear vs mipmap, gamma) → closed loop tin proxy sai | M0 golden tests + spot-check osu! thật mỗi milestone; blend semantics đã verify từ shader; mipmap chỉ kích hoạt khi minify mạnh — asset emit đúng cỡ hiển thị nên hệ số nhỏ |
| Encode perf: composite + SSIM mỗi frame full-res | Incremental composite (chỉ bbox động); SIMD sẵn của .NET (`Vector<T>`) nếu profile đòi; đo tại M1, target ≤4× realtime 1080p30 8-core *(provisional)* |
| Crossfade banding 8-bit trên gradient cực chậm | Verify per-frame bắt được (floor SSIM tile); fallback chèn keyframe giữa |
| Motion estimation sai → base layer lệch | Closed loop là lưới an toàn: mọi lệch thành dirty → patch đè; worst case = chi phí M1, không bao giờ sai hình |
| Bytes tăng trên noisy content khi fidelity thật thay fidelity giả | Đúng thiết kế (fidelity thắng); JPEG path M3 là đòn bẩy bytes chính cho content nhiễu; preset `low` là van xả cuối do user chọn |
| Sprite lượng lớn alive khi patch-rate cao (thrash trước M3) | M1 chấp nhận, M3 ThrashPlanner + budget; time-disjoint nên draw cost bounded `[R3]` |
| GlobalMotionEstimator phức tạp hơn ước lượng (CV thật trên footage nhiễu) | M2 spike-first (§6.3); accept gate 2 tầng; reject = fallback sạch về M1 |
| Baker×MotionLayer compose sai (single-writer rule bị vi phạm khi refactor) | `PropertyOverlapValidator` fail compile trên mọi overlap — lỗi này không thể ship im lặng |

---

## 11. Open questions cho review

Tất cả đã chốt (Q1/Q3/Q4 sau red-team; Q2/Q5/Q6 theo review của user):

- ~~Q1 preset numbers~~ → **M0.5 calibration spike chốt bằng dữ liệu** (§9), không đoán tay.
- ~~Q3 verify-fail~~ → **luôn ghi output + violation report, exit code ≠ 0** — không vứt artifact
  của compile nhiều giờ (§6.4).
- ~~Q4 requested fps~~ → closed loop phân tích tại source fps; verify luôn so tại source-fps
  timestamps; flag chỉ còn ảnh hưởng Animation frameDelay (§6.4).
- ~~Q2 JPEG~~ → **có, dạng candidate trong per-asset encoding search** (PNG / PNG+dither / JPEG(q)
  cùng render + verify SSIM gate, chọn bytes nhỏ nhất) — không bao giờ ép toàn bộ sang JPEG, không
  thêm format mới (WebP...) khi chưa verify osu! support. (M3.)
- ~~Q5 motion sub-scene~~ → **có, do estimator quyết**: một scene `static → pan → static` được
  segment theo thời gian tại điểm motion model đổi (tự nhiên rơi ra từ trajectory breakpoints /
  model-change detection trong M2), mỗi segment chọn representation riêng. Là representation
  optimization, KHÔNG phải scene detection cứng — không tách segment cho thay đổi motion nhẹ
  (overhead keyframe tự trả giá trong cost, ladder tự quyết).
- ~~Q6 shared-decode~~ → **N=1 là implementation constraint của v2.0, không phải architectural
  principle**: tách decode pipeline (`FrameSource`, một decode) khỏi per-target
  `ReconstructionState` ngay từ M1 (frame decoded phân phối cho N consumer qua interface, v2.0 chỉ
  wire N=1) — đường quay lại shared decode mở sẵn, không khóa kiến trúc.

---

## 12. Thứ tự việc cho implementer

M0 → M0.5 → M1 → M2 → M3 → M3.5 checkpoint tuần tự, mỗi milestone một nhánh + A/B bảng vào `docs/research.md` (append, format
sẵn có). Không milestone nào bắt đầu khi acceptance của milestone trước chưa xanh. Fixtures synthetic
tạo bằng ffmpeg script trong `tests/fixtures/` (pan: `-vf zoompan`; dissolve: 2 ảnh `xfade`).
