# OsbMpeg — Representation Research (đường tới end-state)

> Trạng thái: research deliverable, trả lời 12 research questions của concept "Storyboard Compiler /
> Scene Reconstruction Optimizer". Nguồn: facts osu! `[R1]-[R10]` (docs/plan-v2.md §3), số đo v1
> (docs/research.md), và literature CV/codec cổ điển (layered motion — Wang & Adelson 1994, flexible
> sprites — Jojic & Frey 2001, sequential RANSAC, mosaicking, RDO). Kết thúc bằng architecture đề
> xuất (§7) và các hướng bác bỏ kèm lý do (§8). Đây là research, không phải spec — plan-v2.md vẫn là
> spec triển khai; §7 sửa roadmap của nó.

---

## 0. Kết luận chính

1. **Một khung thống nhất bao trùm gần hết các hướng đề xuất**: *layered similarity-motion
   decomposition*. Global camera = layer K=1. "Entity" = layer có support nhỏ. Parallax = các layer
   khác biên độ translation. Panorama = layer tích lũy theo thời gian. Occlusion = byproduct của
   z-order giữa các layer, không cần feature riêng. → Không cần chọn giữa "object tracking vs motion
   layers vs depth": chúng là một trục K và một trục lifetime của cùng một cơ chế.
2. **Đơn vị nguyên tử của storyboard là hình chữ nhật (quad) với similarity transform + alpha**
   — không mask, không shear, không perspective. Mọi decomposition mịn hơn mức đó (pixel-accurate
   segmentation, semantic detection) là over-delivery: không tiêu được. Điều này quyết định chọn
   classical block-level CV thay vì learned models (§3.7).
3. **Optimization đúng cỡ bài toán**: matching pursuit cho decomposition (thêm layer khi marginal
   benefit > marginal cost, đo thật) + representation ladder per region (thử từ rẻ đến đắt, nhận cái
   đầu tiên đạt floor) + DP/RDP cho keyframe 1D + closed loop verify. Không cần RDO Lagrangian,
   graph optimization hay beam search — vì fidelity là hard constraint (không phải trade-off λ) và
   candidate set per vùng nhỏ (≤7), thử trực tiếp rẻ hơn mô hình hóa. §4.
4. **Nội dung nhiễu/grain là giới hạn vật lý của representation này** — không hướng nào trong 12
   hướng cứu được (nhiễu phá motion coherence, dedupe, VÀ crossfade cùng lúc). Đòn bẩy duy nhất:
   JPEG assets + preset. Phải nói thật trong docs sản phẩm. §6.
5. **Roadmap sửa**: M0→M3 giữ nguyên (nền correctness + cost). Sau M3: **checkpoint đo lường với
   attribution report** (spec §7.3) quyết định hướng kế trong menu {LayeredMotion, Panorama,
   CanonicalAssetReuse, PerspectiveStrips} bằng số liệu, không mặc định "M4 = entity tracks".

---

## 1. A — Representation taxonomy

### 1.1 Inventory (những gì storyboard THẬT SỰ biểu diễn được)

| # | Representation | Cấu tạo | Bytes | Workload | Thắng trên |
|---|---|---|---|---|---|
| T1 | **StaticPatch** | 1 sprite, asset crop, span [t0,t1) | 1 asset | 1 alive, fill = area | nội dung tĩnh |
| T2 | **PatchSequence** | chuỗi T1 time-disjoint cùng vùng | N assets | 1 alive | fallback vạn năng (= v1) |
| T3 | **Crossfade** | 2 sprite chồng, `F` 0→1 trên cái mới | 2 assets/cặp | 2 alive trong fade | thay đổi chậm/mượt: dissolve, lighting, deformation chậm |
| T4 | **TransformTrack** | 1 sprite + keyframed `MX`/`MY`/`S`/`V`/`R` (linear easing) | 1 asset + ~bytes text | 1 alive | chuyển động rigid/similarity — nội suy LIÊN TỤC tại refresh rate `[R1]`, mượt hơn source fps |
| T5 | **MotionLayerSet** | K sprite T4, z-ordered | K assets | K alive, fill ≈ K× vùng phủ | parallax, foreground/background tách chuyển động |
| T6 | **Panorama** | asset lớn (tile thành strips) + 1 M track chung | ≈ union nội dung | strips alive, fill ≈ phần trên màn | camera pan/scroll dài |
| T7 | **Animation** | flipbook, frameDelay | N frames, KHÔNG dedupe | N textures resident RAM `[R4]` | thrash thực sự (mọi frame khác nhau) |
| T8 | **Photometric** | `F`/`C` trên sprite sẵn có | ~0 | 0 mới | fade in/out, tint, đổi sáng toàn vùng |
| T9 | **AdditiveLayer** | sprite + `P,A` (blend cộng) | 1 asset | +1 alive | glow, flash, ánh sáng — thứ duy nhất LÀM SÁNG THÊM được (C chỉ nhân tối đi) |
| T10 | **SpriteReuse** | 1 asset, nhiều instance/命 khác transform/time | 1 asset | như số instance | motif lặp: particle giống nhau, UI, texture lặp |
| T11 | **SolidColor** | asset đơn sắc ~16×16 scale to | ~0 | fill theo vùng | letterbox, fade-to-black, nền phẳng |
| T12 | **AlphaMatteSprite** | asset PNG có alpha (mép feather) | 1 asset | fill = **cả quad**, kể cả texel trong suốt `[R3]` | mép mềm cho foreground layer (T5) — bắt buộc để layer không lộ viền chữ nhật |
| T13 | **FlipReuse** | `P,H`/`P,V` | 0 | 0 | nội dung đối xứng (hiếm, gần như free để check) |

### 1.2 Giới hạn biểu diễn CỨNG (không có workaround rẻ)

Linear part của transform trong osu!framework = `R(θ)·diag(sx,sy)` (rotation SAU scale, một lần) —
**5 tham số, thiếu shear** so với affine 6 tham số (`SVD: A = R1·Σ·R2` cần rotation cả trước lẫn
sau scale). Hệ quả:

- **Không shear, không perspective/homography** per sprite. Approximate = subdivide thành strips,
  mỗi strip một similarity (§3.6) — cost tuyến tính theo số strips, error ~ O(h²·độ cong perspective).
- **Không mask/clip tùy ý** — wipe transition may mắn biểu diễn được bằng sprite scene-B trượt vào
  (M track, mép cứng); iris/shape wipe thì không (fallback crossfade/patches).
- **Không gradient tint** — `C` là uniform per sprite; gradient = phải nằm trong asset.
- **Không blur primitive** — đổi focus = crossfade sang asset đã blur sẵn (T3 lo được, tốn asset).
- **Không particle system** — mỗi hạt một sprite (T10 giúp asset, không giúp sprite count).
- **`C` chỉ nhân (làm tối)** — brighten quá mức gốc chỉ có T9 additive.
- Alpha per-pixel TRONG asset: có (PNG). Nhưng fill-rate tính cả texel trong suốt `[R3]` → rect
  phải bó sát, feather mỏng.

### 1.3 Compositions đáng giá (không hiển nhiên)

- **T3∘T4 — crossfade giữa hai TransformTrack**: xấp xỉ "appearance đổi TRONG KHI di chuyển"
  (morph thô). Quan trọng vì deformation chậm + motion là ca phổ biến (người đi bộ xa camera).
- **T6∘T5 — panorama làm layer đáy của MotionLayerSet**: pan + foreground độc lập.
- **T8 trên mọi thứ**: fade scene không đụng asset nào.
- **T2 trên mọi thứ**: closed-loop residual patches là tầng đáy phủ mọi sai số của các tầng trên —
  chính nó biến mọi tầng trên thành *optimization thuần* (sai không gây artifact, chỉ gây tốn).

---

## 2. B — Decomposition strategies: so sánh & verdicts

### 2.1 Khung thống nhất: layered similarity motion

Bài toán: gán mỗi block (16×16) của mỗi frame vào 1 trong K motion models (similarity `(tx,ty,s,θ)`)
+ residual. Thuật toán kinh điển, không ML:

```text
flow blocks (block matching pyramidal, §plan M2)
   → sequential RANSAC: fit dominant model, loại inliers, fit tiếp (K ≤ 4)
   → spatial coherence filter: layer phải là blob liên thông ≥ min area (loại "layer" rác từ noise)
   → temporal: assignment frame trước làm prior frame sau (layer identity = liên tục theo thời gian)
```

- K=1 chính là GlobalMotionEstimator của plan M2 — **build M2 như case K=1 của interface này**,
  không phải special case chết.
- "Entity identity" cho mục đích của chúng ta = *layer tồn tại liên tục* — không cần re-ID phức tạp;
  layer biến mất rồi quay lại được xử lý bằng asset reuse (§2.4), không phải tracking.

### 2.2 Bảng verdict

| Chiến lược | Thắng trên | Chi phí compute | Rủi ro | Verdict |
|---|---|---|---|---|
| Global motion (K=1) | pan/zoom camera — chiếm đa số footage game/cinematic | thấp | đã có gate 2 tầng trong plan | **M2, giữ** |
| Motion layers (K≤4) | parallax, foreground tách nền | trung bình (sequential RANSAC trên flow đã có) | phân mảnh layer do noise; mép layer cần alpha matte T12 | **Ứng viên chính sau checkpoint** — chỉ build khi attribution (§7.3) chỉ ra |
| Entity/object tracking (semantic) | — | cao (detector+tracker+re-ID) | over-delivery: output là mask/box, storyboard chỉ tiêu được rect+similarity; identity dài hạn không có payoff riêng (asset reuse rẻ hơn, §2.4) | **Bác bỏ** (§8.1) |
| Depth estimation | thứ tự z cho layers | cao (learned model) | z-order suy được từ chính occlusion của motion layers (layer nào đè lên khi giao nhau); depth chỉ còn giá trị khi motion mơ hồ | **Bác bỏ cho core; ghi chú niche** |
| Panorama/mosaic | pan dài, camera scroll (rất phổ biến trong MV/anime/game) | trung bình (đã có global transforms → warp+accumulate median) | movers nhiễm vào stitch (median + mask loại); texture limits (§2.5); parallax trong nền phá stitch (→ chỉ nhận khi K=1 residual thấp) | **Ứng viên chính sau checkpoint** |
| Occlusion reasoning | — | ~0 | — | **Byproduct, không phải feature** (§2.3) — không build gì riêng |
| Photometric (fade/tint) | fades, flashes | ~0 | ít | **M1 (P5), giữ** |
| Residual patches | mọi thứ còn lại | — | — | **Tầng đáy vĩnh viễn** — mọi chiến lược trên là optimization so với nó |

### 2.3 Occlusion: tại sao là byproduct

Với layers + z-order + closed loop, mọi yêu cầu của mục "Occlusion/Layer Ordering" tự thỏa:

- *A che B → B không cần update*: đúng theo nghĩa đen — closed loop đo error trên **composite**;
  vùng B bị che không đóng góp error → không patch nào được chi. Tự động, không cần detector.
- *A biến mất → B tiếp tục*: sprite B lifetime dài, nội dung vùng lộ ra được verify ngay frame lộ
  (ErrorMap chạy sau `R.AdvanceTo`) — nếu nội dung B đã đúng (đã thấy trước đó, hoặc panorama tích
  lũy được) thì zero cost; nếu chưa từng thấy → patch, là điều đúng phải làm.
- Điều DUY NHẤT cần chủ động: **kéo dài lifetime của layer bị che thay vì đóng nó** — một quyết
  định trong representation selector (giữ sprite alive qua occlusion nếu dự đoán sẽ lộ lại và nội
  dung còn đúng), đo bằng chính closed loop. Một heuristic nhỏ, không phải hệ thống.

### 2.4 Entity identity → canonical asset reuse (thay thế tracking)

Payoff thực của "same visual entity" là **reuse asset**, và có đường rẻ hơn tracking nhiều:

- Đã có sẵn: `AssetStore` content-hash = re-ID *miễn phí* cho nội dung byte-identical (đã hoạt động
  cross-scene, cross-file).
- Mở rộng: **canonicalization trước hash** — normalize patch về pose chuẩn (scale về bậc lượng tử
  hóa 2^(1/4), khử rotation nếu dùng) rồi hash; instance đặt lại bằng `M`/`S`/`R` của sprite. Cùng
  ngôi sao ở 5 vị trí/3 cỡ = 1 asset + 5 instance. Vẫn là exact-hash (giữ nguyên mô hình AssetStore,
  không fuzzy search O(N²)).
- Near-duplicate thật sự (nội dung gần giống): lookup ứng viên bằng coarse hash → verify bằng
  SSIM-vs-floor → nhận nếu đạt. Bounded, an toàn với hard floor. **Defer sau checkpoint** — chỉ đáng
  khi attribution chỉ ra asset bytes trùng lặp gần-giống lớn.

### 2.5 Panorama: số học khả thi

- Pan 10s @100 osu!px/s trên nền 1080p → pano ~2920×1080: vượt 1024 (mất atlas) nhưng dưới 4096.
  **Tile thành strips ≤1024 wide**, tất cả strips share một trajectory (giữ breakpoints đồng bộ —
  cùng điều kiện với RDP joint §plan-5.3). Strips ngoài màn: GPU clip → fill ~0, chỉ tốn alive-count
  nhỏ. Pan dài tùy ý = thêm strips, không đụng MaxTextureSize.
- Stitch: warp các frame về hệ pano bằng chính global transforms (đã có từ K=1), tích lũy
  **median** per pixel (loại movers), kèm confidence mask — pixel chưa từng thấy = trong suốt, closed
  loop patch khi lộ. Asset bytes ≈ union nội dung — cận dưới lý thuyết của mọi cách encode pan.
- Điều kiện nhận: scene K=1 accept + residual thấp (nền thực sự rigid). Parallax trong "nền" → từ
  chối pano, dùng T5 hoặc để patches.

### 2.6 Depth/parallax không cần depth model

Camera lateral + cảnh tĩnh → mỗi dải depth là một similarity translation khác biên độ — **sequential
RANSAC tự tách chúng thành các layer K=2..4** (parallax chính là "khác translation"). Z-order: layer
nào thắng (đè) tại vùng giao = layer trước; nếu không có vùng giao thì thứ tự không quan sát được và
cũng không ảnh hưởng kết quả render. Không cần depth estimation. Perspective mạnh (mặt đất lao về
camera): similarity per layer sai hệ thống → hoặc strips subdivision (§1.2) hoặc để patches trả —
**quyết bằng attribution, không quyết trước**.

### 2.7 Learned models: verdict

Bác bỏ cho v2 core, vì **granularity mismatch**: SAM/RAFT/depth-anything cho mask/flow/depth
pixel-accurate, nhưng storyboard chỉ tiêu được *rect + similarity + alpha* — phần chính xác vượt mức
rect là thông tin vứt đi, trong khi giá là dependency ONNX runtime + model trăm MB + inference
per-frame + GPU. Block-level classical (block matching + RANSAC) khớp đúng độ phân giải quyết định
của representation. Cửa quay lại (ghi rõ để khỏi tranh cãi lại từ đầu): nếu checkpoint cho thấy
motion layers fail chủ yếu do **flow chất lượng thấp trên nội dung khó** (không phải do model K hay
mép layer), thử DIS flow (classical, nhẹ) trước, learned flow sau cùng.

---

## 3. C — Optimization strategy

### 3.1 Cấu trúc bài toán

Search space phân rã tự nhiên thành 4 tầng gần-độc-lập:

```text
(1) Decomposition per scene:      chọn tập layers (K models + panorama? + segment ranh giới thời gian)
(2) Representation per (vùng, khoảng): chọn T1..T13 cho từng dirty region
(3) Keyframe placement per track: nén trajectory/crossfade span
(4) Asset encoding per asset:     PNG / PNG+dither-palette / JPEG(q)
```

### 3.2 Thuật toán per tầng — và tại sao đủ

| Tầng | Thuật toán | Vì sao không cần nặng hơn |
|---|---|---|
| (1) | **Matching pursuit**: thêm layer/panorama khi *marginal benefit đo thật* (dự đoán giảm residual-patch cost) > *marginal cost* (asset + workload của layer) — chính là generalization của gate-2 §6.3 plan | Số ứng viên nhỏ (K≤4, pano có/không); benefit đo được rẻ trên downscale; sai → closed loop trả bằng patches, không bằng artifact |
| (2) | **Ladder**: thử từ rẻ → đắt (T8 → reuse T10/T13 → T4 → T3 → T1 → T7), nhận cái ĐẦU TIÊN đạt floor | Fidelity là hard constraint ⇒ bài toán là *constrained min cost*, không phải trade-off λ (RDO Lagrangian giải bài khác); cost các bậc thang cách nhau hàng bậc độ lớn ⇒ greedy ladder ≈ optimal |
| (3) | RDP joint screen-space (đã trong plan) cho tracks; greedy grow-until-floor-violated cho crossfade span | Error đơn điệu theo span ⇒ greedy tối ưu; keyframe 1D nếu cần chặt hơn: DP O(n²) — chỉ khi đo thấy RDP bỏ phí |
| (4) | Per-asset: thử các encoding, verify SSIM floor, chọn bytes nhỏ nhất (Q2 đã chốt kiểu này) | Độc lập hoàn toàn per asset, exhaustive là đúng nghĩa đen |

**Bác bỏ** (đến khi có bằng chứng greedy bỏ phí >10-15% trên corpus): global graph optimization,
beam search, DP toàn cục, RDO λ-sweep. Lý do chung: interaction giữa các quyết định đã bị chặn bởi
kiến trúc phân tầng (residual patches hấp thụ mọi hệ quả xấu thành *chi phí đo được* — nghĩa là
greedy nhìn thấy giá thật của mình ngay), và mọi phương pháp toàn cục đều cần cost model *dự đoán*
thay vì *đo* — nguồn sai mới, đúng loại research.md từng phải trả giá (heatmap spike: estimate đẹp,
đo thật thua 2.6-5.4×).

### 3.3 Closed loop trong bức tranh này

Đúng vai user đặt: **safety/correctness mechanism** — floor enforcement + máy đo cost thật cho mọi
tầng trên. Không phải "ultimate algorithm": trí tuệ nằm ở (1)(2)(3)(4); closed loop bảo đảm mọi trí
tuệ đó *không thể sai thành artifact, chỉ có thể sai thành tốn* — đó là điều biến kiến trúc phân
tầng greedy thành khả thi.

---

## 4. D — Cost model

**Objective** (thứ tối thiểu hóa):

```text
minimize   AssetBytes + OsbBytes
```

(AssetBytes trội thực nghiệm: 735MB vs 9.31MB trên minecraft — trọng số giữa hai số này không
thành vấn đề trong thực tế.)

**Hard constraints** (không phải trọng số — giới hạn renderer là vách đá, không phải dốc):

```text
∀ frame:      PSNR ≥ preset.P  ∧  worst-tile SSIM ≥ preset.S        [fidelity — tối thượng]
∀ t:          SteadySbLoad(t) ≤ 4.0 viewport                        [fill-rate `[R3]`]
              TransientSbLoad(t) ≤ +2.0 (fade windows)
              AliveSprites(t) ≤ 500                                 [update walk]
Σ animation:  ResidentBytes ≤ 200MB/scene                           [`[R4]`]
∀ asset:      dim ≤ 1024 ưu tiên (atlas `[R6]`), ≤ 3840 tuyệt đối
.osb:         lines ≤ ~1M, bytes ≤ ~10MB/phút (soft `[R10]`)        [load time]
```

Multi-objective theo nghĩa: objective 1 chiều + constraint vector — KHÔNG weighted sum các trục
workload (một storyboard "trung bình tốt" nhưng vượt fill-rate ở một đoạn vẫn lag ở đoạn đó; peak
là thứ người chơi cảm nhận).

---

## 5. E — Failure modes matrix

| Content | Cơ chế fail | Representation đỡ được | Worst case (chấp nhận) |
|---|---|---|---|
| Hard cut | mọi model chết cùng lúc | scene detection (có sẵn) + I-frame | 1 full-frame asset/cut — đúng giá |
| Fast motion | flow không bắt kịp, crossfade sai công cụ | T7 animation (cap) / T2 patch rate cao | ~NaiveBaseline cục bộ, floor vẫn giữ |
| Camera rotation in-plane | — | T4 có `R` | OK |
| Perspective / out-of-plane | similarity thiếu shear (§1.2) | strips subdivision; hoặc T2 | patches trả, đo attribution rồi mới build strips |
| Deformation chậm (mặt, vải) | không primitive nào biến dạng | **T3 crossfade — tốt bất ngờ** (nội suy tuyến tính giữa 2 pose ≈ optical-flow-free morphing cho Δ nhỏ) | T2 khi nhanh |
| Particles/smoke/fire | hàng trăm mover nhỏ | T10 reuse (particle giống nhau) + T9 additive (fire/glow là additive tự nhiên!) + T7 vùng | T7/T2, budget cap — "không phải mọi thứ thành object" đúng ở đây |
| Reflections/specular | phi-rigid, phi-photometric | không — T2 | patches |
| Lighting change toàn cục | — | T8 (`F`/`C`), T9 (sáng thêm) | gần free |
| Dissolve transition | — | T3 native | free-ish |
| Wipe transition | — | sprite scene-B + M track (mép cứng OK) | T2 nếu wipe hình dạng lạ |
| **Noise/grain** | phá coherence + dedupe + crossfade ĐỒNG THỜI (v1 đã đo: birdbrain tuner bó tay, bad_apple 10k sprites/5s) | **không hướng nào trong 12 hướng cứu được** | JPEG assets + preset thấp hơn; ghi thật vào docs: nội dung grain nặng là ngoài phạm vi hiệu quả của storyboard encoding |
| Occlusion | — | byproduct §2.3 | — |

Noise là ranh giới của toàn bộ approach — nói rõ một lần, đừng để mỗi milestone "phát hiện" lại.

---

## 6. F — Recommended architecture

### 6.1 Pipeline end-state (conceptual, khớp sơ đồ của concept doc)

```text
                     VIDEO
                       │ decode source-res (FrameSource — tách khỏi consumer, Q6)
                       ▼
             SCENE / SEGMENT ANALYSIS          ← cuts (có sẵn) + motion-segment ranh giới (Q5)
                       │
                       ▼
             LAYERED MOTION FIT (K=0..4)       ← matching pursuit, K=1 là M2 hiện tại
                       │        └── panorama accumulate khi đủ điều kiện §2.5
                       ▼
             REPRESENTATION LADDER             ← per dirty-region: T8→T10→T4→T3→T1→T7
                       │
                       ▼
             KEYFRAME / SPAN COMPRESSION       ← RDP joint, grow-until-floor
                       │
                       ▼
             ASSET ENCODING SEARCH             ← PNG/dither/JPEG per asset, SSIM gate
                       │
                       ▼
             CLOSED-LOOP VERIFY (floor) ───── fail → chi thêm ở đúng chỗ (tầng đáy patches)
                       │
                       ▼
             .osb + assets + Quality/Workload report
```

Mọi hộp trên đã có chỗ đứng trong plan-v2 trừ LayeredMotion K>1 / Panorama / CanonicalReuse /
Strips — bốn cái đó là **menu của checkpoint**, không phải cam kết.

### 6.2 Roadmap sửa đổi

```text
M0 harness → M0.5 calibration → M1 closed-loop → M2 motion K=1 (build như case K=1
của layer interface) → M3 budgets/formats → ★ M3.5 ARCHITECTURE CHECKPOINT ★
                                                  │ attribution report (§6.3)
                    ┌─────────────┬───────────────┼───────────────┬─────────────┐
                    ▼             ▼               ▼               ▼             ▼
              LayeredMotion   Panorama     CanonicalAsset   Perspective    "đủ rồi"
                 (K>1)        (T6)            Reuse           Strips      (ship v2)
```

### 6.3 M3.5 Attribution report — spec (để checkpoint chạy được bằng số, không bằng cảm giác)

Sau M3, trên toàn corpus, phân loại **mọi byte asset còn lại** theo nguyên nhân:

| Bucket | Cách đo |
|---|---|
| `local-coherent-motion` | dirty blocks có flow cluster nhất quán ≠ camera model (sequential RANSAC pass 2 tìm được model K=2 giải thích ≥70% block đó) |
| `pan-reveal` | dirty blocks nằm trong dải mép theo hướng camera translation |
| `perspective-residual` | blocks inlier với camera model ở tâm nhưng residual tăng đơn điệu theo khoảng cách tâm (chữ ký của thiếu shear/perspective) |
| `near-duplicate-assets` | % bytes của assets có coarse-hash trùng asset khác nhưng exact-hash khác |
| `deformation/other` | phần còn lại có structure (SSIM giữa refresh liên tiếp cao) |
| `noise` | phần còn lại không structure (SSIM giữa refresh liên tiếp thấp) — bucket "không cứu được" |

Quyết định: build hướng có bucket lớn nhất nếu bucket đó ≥ ~25-30% tổng bytes corpus *(provisional)*;
`noise` trội → dừng, ship, ghi giới hạn vào docs. Nhiều bucket lớn → thứ tự theo (bytes ÷ effort).

### 6.4 Điều chỉnh vào plan-v2.md (đã áp dụng cùng commit với doc này)

- Q2/Q5/Q6 chốt theo review của user (JPEG = candidate trong asset-encoding search; motion-segment
  sub-scene do estimator quyết; N=1 là implementation constraint — decode tách khỏi reconstruction
  state từ M1).
- M4 cũ (gated entity tracks) thay bằng **M3.5 checkpoint + menu** như trên.
- M2 build motion dưới interface layer-set (K=1 hôm nay, K>1 không phải viết lại).

---

## 7. Những hướng BÁC BỎ (kèm lý do — đừng mở lại nếu không có bằng chứng mới)

1. **Semantic object detection/tracking làm decomposition chính** — granularity mismatch (§2.7):
   storyboard tiêu rect+similarity, không tiêu mask/label; payoff identity dài hạn đã có đường rẻ
   hơn (canonical asset reuse §2.4); dependency + compute lớn. "camera + 4 motion layers + patches"
   thắng "camera + 37 semantic objects" đúng như concept doc dự cảm — vì 37 objects vẫn phải nắn về
   rect similarity trước khi emit, thành 37 layers chất lượng thấp hơn 4 layers fit thẳng trên flow.
2. **Depth estimation model cho z-order** — z-order suy được từ chính dữ liệu occlusion khi layers
   giao nhau; khi không giao, z-order không quan sát được và không ảnh hưởng output (§2.6).
3. **Global optimization (graph/DP toàn cục/beam/λ-RDO)** — bài toán là constrained-min với hard
   floor, tầng đáy patches làm mọi sai lầm greedy thành chi phí đo được thay vì artifact; corpus
   phải chứng minh greedy bỏ phí đáng kể trước đã (§3.2). Bài học heatmap-spike: model dự đoán cost
   từng thua đo thật 2.6-5.4×.
4. **Fuzzy/perceptual near-duplicate matching trực tiếp** (embedding NN search) — O(N²)/index phức
   tạp + rủi ro floor; canonical-exact rồi SSIM-gated lookup phủ phần lớn giá trị với 1/10 độ phức
   tạp (§2.4).
5. **Pixel-accurate segmentation (SAM et al.) cho layer mask** — mask chỉ dùng được ở mức
   rect+alpha-feather; block-level coherence là toàn bộ thông tin tiêu được (§2.7).
6. **Mesh/subdivision approximation tổng quát** (lưới sprite biến dạng) — nổ sprite count/fill-rate
   (mỗi ô lưới một quad alive), thiếu shear nên lưới thô sai hệ thống; chỉ giữ dạng hạn chế nhất:
   horizontal strips cho perspective, và cũng chỉ sau attribution (§2.6).
7. **[Variables] / mọi thủ thuật nén .osb text đổi bằng decode cost** — `[R9]`, 3s→45s là bằng
   chứng đóng.

## 8. Decision Matrix

Một dòng một hướng. Decision: `MUST` (implement, đã lên blueprint) / `INVESTIGATE` (spike có scope)
/ `DEFER` (chờ data nêu tên) / `REJECT` (evidence chống). "Flips it" = điều kiện đảo quyết định.

| Direction | Evidence hiện có | Benefit kỳ vọng | Complexity | SB compat | Decision | What flips it |
|---|---|---|---|---|---|---|
| Closed-loop + absolute floor | v1 open-loop là root cause mờ (§plan-2.1) | hết mờ by construction | trung bình | hoàn hảo (IR-level) | **MUST (M1)** | — |
| Crossfade (T3) | blend lerp verify khớp shader; dissolve là pattern phổ biến | giảm mạnh refresh rate vùng đổi chậm | thấp | native (`F`) | **MUST (M1)** | — |
| Photometric (T8) | fade/tint = command native, ~0 bytes | fades gần free | thấp | native (`F`/`C`) | **MUST (M1)** | — |
| Global motion K=1 (T4) | `[R1]` nội suy liên tục; minecraft 79k sprites là bằng chứng thiếu nó | thắng lớn nhất trên pan/zoom | trung bình | native (`M`/`S`) | **MUST (M2)**, spike flow trước | S2.0 spike cho thấy flow không dùng được trên corpus → thu hẹp còn synthetic/game content |
| Motion sub-segmentation (Q5) | scene static→pan→static là ca thường | mỗi segment representation đúng | thấp (trajectory đã có) | — | **MUST (M2)** | — |
| Layered motion K>1 (T5) | lý thuyết vững (sequential RANSAC); chưa đo tỷ trọng trên corpus | lớn nếu bucket `local-coherent-motion` lớn | trung bình-cao (mép layer, alpha matte) | tốt (K sprites z-ordered) | **DEFER** → M3.5 | bucket ≥25-30% bytes |
| Panorama (T6) | toán khả thi §2.5; strips giải texture limits | cận dưới bytes cho pan dài | trung bình (stitch median + mask) | tốt (strips + M chung) | **DEFER** → M3.5 | bucket `pan-reveal` ≥25-30% |
| Canonical asset reuse | AssetStore hash sẵn; canonicalization là mở rộng key (I10) | lớn nếu near-dup bucket lớn | thấp-trung bình | hoàn hảo | **DEFER** → M3.5 | bucket `near-duplicate-assets` ≥~20% |
| Perspective strips | similarity thiếu shear (§1.2) — sai hệ thống đo được | vừa, content-dependent | trung bình | chấp nhận (K quads/plane) | **DEFER** → M3.5 | bucket `perspective-residual` ≥25% |
| Occlusion reasoning riêng | §2.3: byproduct của z-order + closed loop | 0 (đã có free) | — | — | **REJECT** (as feature) | phát hiện ca closed loop không tự xử lý được |
| Semantic object detection/tracking | granularity mismatch §2.7; payoff identity đã có đường rẻ §2.4 | thấp so với giá | cao + dependency nặng | kém (SB không tiêu mask/label) | **REJECT** | một corpus thật nơi 4-layer motion thua rõ per-object tracks SAU KHI layered motion đã thử |
| Depth estimation model | z-order suy từ occlusion data §2.6 | thấp | cao (learned dep) | gián tiếp | **REJECT** | layered motion bế tắc riêng vì thứ tự z không suy được |
| Learned optical flow (RAFT...) | classical khớp granularity rect §2.7 | chỉ khi classical fail | cao (ONNX, model, GPU) | — | **REJECT** (v2) | S2.0 + DIS flow đều fail trên bucket lớn |
| RDO Lagrangian / global graph search | fidelity là hard constraint không phải λ; heatmap-spike bài học predict-vs-measure | thấp | cao | — | **REJECT** | đo được greedy ladder bỏ phí >10-15% trên corpus (cần instrument so sánh oracle nhỏ) |
| DP keyframe placement | RDP đủ theo mọi đo hiện có | nhỏ | thấp | — | **DEFER** | đo thấy RDP bỏ phí keyframes đáng kể |
| Fuzzy/embedding near-dup search | O(N²)/index + rủi ro floor; canonical-exact phủ phần lớn §2.4 | nhỏ so canonical | cao | — | **REJECT** | canonical reuse ship rồi mà near-dup bucket vẫn lớn |
| Animation fallback (T7) | thrash thật tồn tại (bad_apple); `[R4]` giới hạn memory | chặn sprite-count nổ | thấp (logic v1 có sẵn) | native, đắt memory | **MUST (M3)**, capped | — |
| JPEG assets | AssetTrimmer DEFLATE data: photographic nén kém PNG; cần verify load | 30%+ bytes trên photo content | thấp | cần OQ-4 verify | **MUST (M3)** sau spike | OQ-4 fail → REJECT |
| WebP/AVIF | chưa có evidence osu load | — | thấp | UNCONFIRMED | **REJECT** (đến khi verify) | xác nhận cả stable+lazer load |
| Grain synthesis | chưa có gì | vượt trần noise (§5) | cao, rủi ro floor | additive layer khả thi | **DEFER** (open research §9) | bucket `noise` trội VÀ user cần content đó |
| Perceptual metric làm floor | concept doc yêu cầu không thay hard gate | — | trung bình | — | **DEFER** | hard gate ổn định nhiều release + case cụ thể hard gate cho kết quả sai cảm nhận |
| Mesh/subdivision tổng quát | nổ sprite count; thiếu shear làm lưới thô sai | — | cao | kém (fill-rate) | **REJECT** | không thấy đường flip |
| [Variables] nén .osb | `[R9]` 3s→45s | âm | — | — | **REJECT** vĩnh viễn | — |

## 9. Open research (ghi lại, không hành động)

- **Grain synthesis**: floor SSIM-tile có thể cho phép "re-noise" — patch nền sạch + lớp grain
  tile lặp (T10) additive/alpha yếu. Về lý thuyết vượt qua giới hạn noise (§5); về thực nghiệm chưa
  có gì. Chỉ đáng nhìn nếu `noise` bucket trội VÀ user cần content đó.
- **WebP/AVIF assets**: chưa xác nhận osu! (stable lẫn lazer) load — verify trước khi bàn tiếp
  (đúng nguyên tắc Q2: không thêm format chỉ vì compression).
- **Perceptual metric (LPIPS-lite) làm floor phụ** — chỉ sau khi hard gate hiện tại chạy ổn định
  qua nhiều release; không bao giờ THAY hard gate (đúng yêu cầu concept doc §11).
