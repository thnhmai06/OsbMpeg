# OsbMpeg v2 — Implementation Blueprint (M0 → M3.5)

> **Tài liệu này là thứ Sonnet đọc để code.** Quan hệ tài liệu:
> - `docs/plan-v2.md` — chẩn đoán v1, facts osu! `[R1]-[R10]`, quyết định cấp plan. Blueprint thắng
>   plan-v2 khi mâu thuẫn về chi tiết implementation.
> - `docs/research-representations.md` — không gian representation (T1-T13), lý do bác bỏ các hướng,
>   decision matrix. Là "tại sao" của mọi quyết định ở đây.
> - `docs/research.md` — lịch sử đo đạc v1. Append kết quả benchmark mỗi milestone vào đó.
>
> **Quy tắc cho implementer**: gặp điểm blueprint không nói rõ VÀ không suy ra được từ invariants
> (§1) → hỏi Opus Advisor, không tự phát minh architecture. Điểm đánh dấu `OQ-n` là open question
> có sẵn phạm vi spike an toàn (§9) — được phép chạy spike trong phạm vi đó.

**Phân loại quyết định** dùng xuyên suốt:

- `MUST` — evidence đủ, implement như spec.
- `SHOULD-INVESTIGATE` — đáng nghiên cứu, chưa khóa; có spike scope.
- `DEFER-UNTIL-DATA` — chỉ quyết sau benchmark/attribution nêu tên.
- `REJECT` — có evidence không đáng làm (lý do trong research-representations.md §7).

---

## 1. Architectural Invariants (I1–I10)

Mọi code v2, hiện tại và tương lai, phải giữ. Vi phạm = bug kiến trúc, không phải lựa chọn style.

| #       | Invariant                                                                                                                                                                                                                                                                                                                                                                               |
|---------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **I1**  | **IR là ngôn ngữ chung duy nhất.** Mọi representation — hiện có hay thêm sau — emit `SbSprite`/`SbAnimation` + commands chuẩn vào `SbDocument`. Không side-channel, không "renderer hint" ngoài IR. Reconstruction và Verification chỉ hiểu IR.                                                                                                                                         |
| **I2**  | **Verification độc lập là authority cuối.** Verify pass parse lại `.osb` từ đĩa + đọc assets từ đĩa + render bằng `SoftwareStoryboardRenderer` — KHÔNG dùng `ReconstructionState` in-memory của encoder (two-implementation rule: R có bug thì verify bắt được). Dùng chung thư viện thấp (`Compositor`, `CommandEvaluator`) là được phép — khác nhau ở orchestration và nguồn dữ liệu. |
| **I3**  | **Fallback chain luôn kết thúc ở PatchProvider** — provider terminal không bao giờ fail (hard refresh crop từ source frame). Mọi optimization fail → rơi xuống bậc dưới → cùng lắm là patch. Đắt được, sai không.                                                                                                                                                                       |
| **I4**  | **Confidence/heuristic không bao giờ thay pixel check.** Motion confidence, hash equality, RDP error, tracking score... chỉ dùng để ĐỀ XUẤT candidate. Mọi `Commit` phải qua `Evaluate` pixel-level đạt floor trước.                                                                                                                                                                    |
| **I5**  | **Per-property single-writer + `PropertyOverlapValidator` gate mọi output.** Mỗi property (X, Y, ScaleX, ScaleY, Rotation, Alpha, Colour, Blending, Flips) của một object có đúng một nguồn ghi; validator fail compile khi phát hiện overlap (chạy sau loop-flatten).                                                                                                                  |
| **I6**  | **Floor tuyệt đối per-frame per-tile.** Budget vượt → đổi representation hoặc báo cáo violation; KHÔNG BAO GIỜ âm thầm hạ floor.                                                                                                                                                                                                                                                        |
| **I7**  | **Thêm representation mới = thêm `ICandidateProvider` (và/hoặc một analyzer đề xuất hypothesis), KHÔNG sửa `ClosedLoopEncoder` core, `ReconstructionState`, hay verifier.** Đây là kết quả backward-architecture-check (§2.5).                                                                                                                                                          |
| **I8**  | **Mọi số *(provisional)* phải calibrate bằng đo trước khi trở thành gate.**                                                                                                                                                                                                                                                                                                             |
| **I9**  | **Attribution sau mỗi milestone lớn; feature mới chỉ sinh ra từ bucket data** (§8), không từ tên feature nghe hay.                                                                                                                                                                                                                                                                      |
| **I10** | **`AssetStore` content-addressed là cơ chế dedupe duy nhất.** Mở rộng (canonicalization, encoding variants) phải là mở rộng của hash key, không phải cơ chế song song.                                                                                                                                                                                                                  |

---

## 2. Target Architecture

### 2.1 Module map (sau M3)

```text
src/OsbMpeg.Parsers/                        GIỮ NGUYÊN toàn bộ (IR, OsbWriter, OsbvParser,
                                            CommandEvaluator, EasingTable, Passes)
                                            + M0: Ir/Passes/PropertyOverlapValidator.cs
                                            + M3: OsbWriter value-chaining shorthand

src/OsbMpeg.Compiler/
├── Compilation/
│   ├── VideoCompiler.cs                    SỬA M1: orchestrate pipeline mới
│   ├── VideoSourcePlanner.cs / VideoSourceKey.cs   GIỮ
│   └── GroupTransformBaker.cs              SỬA M2: BakeTracked (camera fold, §6 S2.5)
├── Detection/
│   ├── ScenePrePass.cs / SceneBounds.cs    GIỮ (TileRunTracker vẫn phục vụ detection)
├── Analysis/                               MỚI M2
│   ├── FlowField.cs                        block matching pyramidal
│   ├── MotionModelFitter.cs                RANSAC similarity, sequential (layer-set interface)
│   ├── MotionSegmenter.cs                  ranh giới thời gian theo model đổi chế độ (Q5)
│   └── TrajectoryFitter.cs                 RDP joint, corner-metric screen-space
├── Encode/
│   ├── ClosedLoopEncoder.cs                MỚI M1: vòng chính per scene/segment
│   ├── ReconstructionState.cs              MỚI M1: display list + canvas incremental
│   ├── ErrorMap.cs                         MỚI M1: blockPSNR 16×16 + tileSSIM 64×64
│   ├── DirtyRects.cs                       MỚI M1: coalescing
│   ├── FrameWindow.cs                      MỚI M1: sliding window decoded frames
│   ├── EmitContext.cs                      MỚI M1: sink + z-order + per-property lastEnd bookkeeping
│   ├── Candidates/
│   │   ├── ICandidateProvider.cs           MỚI M1 (§2.2)
│   │   ├── PatchProvider.cs                MỚI M1 (terminal, I3)
│   │   ├── CrossfadeProvider.cs            MỚI M1
│   │   ├── PhotometricProvider.cs          MỚI M1
│   │   ├── TransformTrackProvider.cs       MỚI M2
│   │   └── AnimationProvider.cs            MỚI M3 (post-scene, ThrashPlanner)
│   ├── AssetStore.cs                       SỬA M3: encoding search (PNG/dither/JPEG), hash gồm encoding
│   ├── WorkloadBudgets.cs                  MỚI M3
│   └── EncodeOptions/Progress/Statistics   GIỮ/GỌN
├── Evaluation/
│   ├── Metrics.cs                          SỬA M0: + TileSsim
│   ├── WorkloadAnalyzer.cs                 MỚI M0
│   ├── QualityVerifier.cs                  MỚI M0: verify stage + subcommand backend
│   └── AttributionAnalyzer.cs              MỚI M3.5
└── Shared/                                 GIỮ: Media/ (FrameSource, MediaProbe, VideoFrame,
                                            FrameWriter), Render/ (Compositor SỬA M0 bilinear,
                                            SoftwareStoryboardRenderer, CanvasVideoFrame),
                                            Analysis/ (TileGrid, TileRunTracker, ContentHasher)

XÓA (M1, xem §5.9 migration): ParameterTuner.cs, TileEncodeLoop.cs, QuadtreeMerger.cs,
AnimationDetector.cs (logic tham khảo lại từ git history khi làm AnimationProvider),
EncodePipeline.cs, NaiveBaseline.cs (giữ nếu calibrate M0.5 cần — xóa sau M0.5),
TileTimeline.cs (nếu không còn consumer ngoài detection — kiểm tra lúc xóa).
```

Tên file là đề xuất có căn cứ (khớp cấu trúc repo hiện tại); **module boundary quan trọng hơn tên**
— đổi tên được, đổi boundary phải hỏi Advisor.

### 2.2 Core interfaces (contract — MUST, chữ ký được phép chỉnh cú pháp, không được đổi ngữ nghĩa)

```csharp
namespace OsbMpeg.Compiler.Encode;

/// Vùng dirty cần biểu diễn: bbox pixel-space (đã coalesce), khoảng thời gian nó "nợ" nội dung.
public sealed record DirtyRegion(
    int X, int Y, int Width, int Height,      // pixel space, source resolution
    double ViolationStartMs,                   // frame vi phạm đầu tiên (backdating, §5 S1.9)
    double NowMs);                             // frame hiện tại của vòng loop

/// Ngữ cảnh encoder đưa cho provider — read-only với provider.
public sealed class EncodeContext
{
    public required FrameWindow Window { get; init; }          // frames decoded gần đây, truy cập theo tMs
    public required ReconstructionState Reconstruction { get; init; }  // đọc trạng thái hiển thị hiện tại
    public required QualityPreset Preset { get; init; }
    public required WorkloadBudgets Budgets { get; init; }     // M3; trước đó là no-op instance
    public required SceneMotionInfo Motion { get; init; }      // M2; K=0 trước đó. Analysis hints —
                                                               // provider dùng để quyết propose hay không (I4:
                                                               // hint không thay pixel check)
    public required AssetStore Assets { get; init; }
}

/// Một cách biểu diễn cụ thể cho một DirtyRegion. Stateless sau khi tạo.
public interface IRepresentationCandidate
{
    CandidateKind Kind { get; }
    long EstimatedCostBytes { get; }          // để sắp thứ tự trong một bậc thang — ước lượng rẻ, không cần đúng tuyệt đối

    /// Render thử composite của candidate này lên vùng bbox tại thời điểm tMs vào scratch buffer.
    /// KHÔNG side effects. Encoder gọi cho mọi frame candidate ảnh hưởng (I4).
    void RenderPreview(Span<byte> rgbScratch, int stride, double tMs);

    /// Ghi sprite/commands vào EmitContext + trả về mô tả để encoder apply vào ReconstructionState.
    /// Chỉ được gọi sau khi Evaluate pass. Trả bytes thật đã ghi (asset mới qua AssetStore).
    CommitResult Commit(EmitContext emit);
}

public interface ICandidateProvider
{
    /// Bậc thang của provider — encoder thử theo thứ tự tăng dần. Patch = int.MaxValue-1 (terminal).
    int LadderRank { get; }
    /// 0..n candidates cho region này. Không side effects. Trả rỗng = "không áp dụng được".
    IEnumerable<IRepresentationCandidate> Propose(DirtyRegion region, EncodeContext ctx);
}
```

```csharp
/// Trạng thái hiển thị mô phỏng — working truth của encoder (KHÔNG phải authority, xem I2).
public sealed class ReconstructionState
{
    // Display list: mọi object đã emit cho scene này, theo declare order (z-order trong layer).
    // Spatial index: grid bucket 128×128 px để query object giao một bbox.
    public void AdvanceTo(double tMs);         // re-composite các bbox có primitive động (fade đang chạy,
                                               // motion track) — thuật toán §5 S1.2
    public void Apply(CommitResult committed); // thêm object mới vào display list + composite ngay vùng nó phủ
    public ReadOnlySpan<byte> Canvas { get; }  // RGB24 source-res
    public IReadOnlyList<ActiveObject> QueryRegion(int x, int y, int w, int h); // cho provider (vd Photometric
                                               // cần biết sprite top-most phủ vùng)
}
```

```csharp
/// EmitContext — chủ sở hữu duy nhất của việc GHI vào SbDocument cho một encode target.
public sealed class EmitContext
{
    // Bảo đảm I5 tại nguồn: track per (object, property) lastEndMs; API thêm command
    // từ chối overlap ngay lúc emit (throw) — validator cuối là lưới thứ hai, không phải lưới duy nhất.
    public SbSprite NewSprite(...);            // declare-order = call-order (z-order convention)
    public void AddCommand(SbObject obj, SbCommand cmd);
}
```

**Vòng chọn candidate trong `ClosedLoopEncoder`** (MUST — đây là chỗ I3/I4/I7 sống):

```text
foreach region in dirtyRegions (sorted by area desc):
    foreach provider in providers (sorted by LadderRank):
        foreach candidate in provider.Propose(region, ctx) (sorted by EstimatedCostBytes):
            ok = mọi frame candidate ảnh hưởng: metric(RenderPreview, source) đạt floor
            if ok và Budgets.Allow(candidate):      # Budgets no-op trước M3
                r = candidate.Commit(emit)
                Reconstruction.Apply(r)
                goto next region
    # không bao giờ tới đây: PatchProvider (rank max) luôn propose 1 candidate luôn pass
```

Provider hiện tại và rank: `PhotometricProvider(10)`, `TransformTrackProvider(20, M2)`,
`CrossfadeProvider(30)`, `PatchProvider(int.MaxValue-1)`, `AnimationProvider` không nằm trong vòng này (post-scene pass,
§7). Thêm representation mới = thêm provider với rank phù hợp (I7). Provider được TỰ QUYẾT không propose dựa trên hints
(vd Crossfade thấy block đổi liên tục qua
`ctx.Motion`/window → trả rỗng ngay, đỡ tốn evaluate) — đó là cách "không khóa cứng thứ tự thang"
mà vẫn giữ correctness: bỏ propose chỉ có thể làm chậm/tốn hơn (rơi xuống patch), không thể làm sai.

### 2.3 Data flow một compile (`VideoCompiler.CompileAsync`)

```text
.osbv parse ──► native Sprite/Animation passthrough ──────────────────────────► SbDocument
            └─► AnimationVideo objects
                  └─► VideoSourcePlanner (probe, group theo file+fps)          [GIỮ]
                        └─► per plan: ScenePrePass window-scoped → ScenePlan[] [GIỮ]
                              └─► per member × per scene-overlap:              [SỬA M1]
                                    FrameSource decode (source res, source fps)
                                      └─► IFrameConsumer (N=1 v2.0, Q6)
                                            └─► [M2] Analysis pass 1 (downscale flow → motion
                                                 model per segment; segment boundaries)
                                            └─► ClosedLoopEncoder per segment:
                                                  frame → R.AdvanceTo → ErrorMap → DirtyRects
                                                  → provider ladder → Commit → R.Apply
                                                  (emit qua EmitContext vào SbDocument,
                                                   assets qua AssetStore)
SbDocument ──► IR passes: MergeAdjacentCommands → DropNoOpCommands → LoopExtractor
           ──► PropertyOverlapValidator (fail = compile fail)                  [M0]
           ──► OsbWriter → .osb                                                [GIỮ]
           ──► QualityVerifier: parse .osb từ đĩa + assets đĩa → render mọi source-fps
               frame → per-frame report; violation → exit code ≠ 0, artifact vẫn ghi  [M0]
           ──► WorkloadAnalyzer report                                         [M0]
```

Ghi chú M2: analysis cần flow giữa frame liên tiếp — chạy TRONG cùng decode stream (giữ 1 frame trước ở downscale),
không decode 2 lần. Motion model per segment chốt xong mới encode segment đó → cần buffer decode theo segment hoặc
decode 2 pass? **Chốt: 2 pass decode cho scene có motion (pass A: downscale-only, nhanh, ra model+segments; pass B:
full-res encode)** — đơn giản, decode downscale rẻ (`-vf scale=256:-2`), tránh giữ cả scene trong RAM. Scene không
motion (spike/gate reject) chỉ 1 pass. (Đây là quyết định MUST — đừng stream-interleave analysis và encode trong v2.0.)

### 2.4 State ownership

| State                     | Owner                                  | Lifecycle                  | Ai khác được đọc                    |
|---------------------------|----------------------------------------|----------------------------|-------------------------------------|
| `SbDocument`              | `VideoCompiler`                        | cả compile                 | passes, writer, verifier (qua file) |
| Ghi command/sprite        | `EmitContext` (duy nhất)               | per encode target          | —                                   |
| `ReconstructionState`     | `ClosedLoopEncoder`                    | per scene-segment × target | providers (read qua ctx)            |
| `FrameWindow`             | `ClosedLoopEncoder`                    | per segment                | providers (read)                    |
| `AssetStore`              | `VideoCompiler` (một instance/compile) | cả compile                 | providers (GetOrAdd)                |
| Motion model / segments   | `Analysis` output, immutable record    | per scene                  | encoder, providers (hints)          |
| Budgets counters          | `WorkloadBudgets`                      | cả compile                 | encoder (Allow), report             |
| Quality/violation records | `QualityVerifier`                      | verify stage               | CLI report, exit code               |

### 2.5 Backward architecture check (acceptance của kiến trúc — ĐÃ PASS, đây là vì sao)

Câu hỏi: *"Tuần sau phát hiện representation mới tốt hơn mọi cái hiện có — thêm được như candidate mà không rewrite
ClosedLoopEncoder/Reconstruction/Verification?"*

- Encoder core chỉ biết `ICandidateProvider`/`IRepresentationCandidate` — thêm = thêm class + đăng ký vào provider list
  với rank (I7). Không sửa vòng loop.
- `ReconstructionState` composite từ **IR objects** (display list), không từ "representation type"
  — mọi candidate Commit ra IR chuẩn (I1) nên R render được nó mà không biết nó là gì. Điều kiện duy nhất: primitive
  động mới (kiểu animation frameDelay, loop) phải nằm trong tập command IR mà
  `CommandEvaluator` đã evaluate được — tập đó là TOÀN BỘ command space của .osb (đã đủ từ v1).
- Verifier render từ `.osb` — theo định nghĩa không biết representation nào sinh ra file (I2).
- Decomposition hypotheses mới (vd depth layers) = analyzer mới ghi thêm vào `SceneMotionInfo`
  (mở rộng record, additive) + provider mới tiêu nó. Encoder không đổi.
- Kẽ hở đã vá trước: nếu ladder là if-chain trong encoder thì I7 chết — vì vậy provider list + rank là **bắt buộc từ
  M1**, không phải refactor sau.

---

## 3. M0 — Verification Harness

**Purpose**: mọi milestone sau cần máy đo tin được trước khi tin bất kỳ số nào. **Dependencies**: không. **Non-scope**:
không đụng encode path hiện tại — v1 vẫn compile được nguyên trạng cho tới M1 (dùng làm baseline A/B).

### S0.1 `Compositor` bilinear — MUST

- **Scope**: thêm sampling bilinear (mặc định mới); giữ nearest qua enum tham số cho test cũ. Sample tại tâm texel,
  clamp mép (không wrap). Alpha của asset (PNG RGBA — M1 patches chưa dùng alpha, T12 sau này dùng) tham gia lerp thẳng
  (straight alpha, khớp blend đã verify).
- **Algorithm**: chuẩn — 4 texel lân cận, trọng số phân số; áp trước tint/blend hiện có.
- **Tests**: identity transform = copy nguyên pixel (bilinear tại tâm texel không đổi giá trị); scale 2× của ảnh 2×2
  biết trước từng pixel kết quả (tính tay trong test); rotation 90° = transpose đúng; property: output không vượt
  min/max các texel nguồn (không ringing).
- **Acceptance**: tests xanh; render fixture `.osb` tay (S0.5 golden) không còn cạnh răng cưa khi scale ≠ 1 (so ảnh
  golden mới).

### S0.2 `Metrics.TileSsim` — MUST

- **Scope**: SSIM per tile 64×64 luma (`luma = (77R+150G+29B)>>8`), grid non-overlap, tile mép co lại theo phần còn lại
  (≥16px mới tính, nhỏ hơn gộp vào tile kề). Trả `(Min, P1, Mean)` + optionally tile index của Min (cho report/debug).
  Công thức SSIM chuẩn (C1= (0.01·255)², C2= (0.03·255)²), mean/var/cov trực tiếp per tile (không gaussian window — tile
  LÀ window).
- **Non-scope**: không thay `Metrics.Ssim` global (giữ cho report), không perceptual metric (REJECT đến khi hard gate ổn
  định — research §8).
- **Tests**: `TileSsim(x,x).Min == 1`; một tile bị phá (invert) → Min tụt mạnh trong khi Mean đổi ít (test chứng minh
  đúng cái global SSIM mù — asserted bằng số cụ thể); ảnh shift 1px → Min giảm rõ.
- **Acceptance**: tests xanh; chạy trên cặp frame v1-vs-source cho thấy tile chứa staleness có SSIM thấp (kiểm bằng mắt
  1 lần, ghi số vào research.md).

### S0.3 `WorkloadAnalyzer` — MUST

- **Contract**: input `SbDocument` (+ asset dimension lookup từ `AssetStore`/đĩa); output record:
  `PeakSteadySbLoad`, `PeakTransientSbLoad` (phần từ sprite đang trong fade window — sprite có F command đang chạy tại
  t), `PeakAliveSprites`, `AnimationResidentBytes` (Σ frames×w×h×4 per animation, max per scene), `TotalAssetBytes`,
  `OsbLineCount`, timeline mẫu (mỗi 100ms) để in đồ thị text.
- **Algorithm**: event sweep — per object: alive span = `[minCmdStart, maxCmdEnd]` (conservative,
  `OQ-5`); visible area tại t = bbox sau transform (evaluate qua `CommandEvaluator` tại các mốc sample 100ms; alpha=0 →
  area vẫn tính vào AliveSprites nhưng không vào SbLoad — khớp `[R3]`/D4). SB load = Σ area / (854×480
  storyboard-space).
- **Tests**: document tay 2 sprite chồng thời gian → peak = 2, load = tổng area đúng; animation resident tính đúng.
- **Acceptance**: chạy trên output v1 minecraft (79k sprites) < 30s, số liệu in được.

### S0.4 `PropertyOverlapValidator` (Parsers, `Ir/Passes/`) — MUST

- **Algorithm**: chạy TRÊN document đã `LoopFlattener.Flatten` (bản copy cho validate — không sửa document thật); per
  object per property (map `SbCommandKind` → property theo bảng §plan-3 [R5]:
  M→{X,Y}, MX→X, S→{ScaleX,ScaleY}, V→{ScaleX,ScaleY}...): sort theo StartMs, lỗi nếu
  `next.StartMs < prev.EndMs - ε` (ε=0.5ms). Zero-duration commands cùng mốc: cho phép đúng 1.
- **Failure mode**: throw `OsbValidationException` với object index + property + 2 khoảng chồng — compile fail. Không
  auto-fix.
- **Tests**: case chồng M vs MX; S vs V; trong loop (flatten ra chồng); case hợp lệ back-to-back (`end==start`) pass.

### S0.5 `QualityVerifier` + `verify` subcommand + golden fixtures — MUST

- **Contract**: verification là **stage bắt buộc trong `CompileAsync`** (sau OsbWriter). Subcommand
  `verify <input.osbv> <output.osb> <assets>` (hidden) chạy lại stage đó standalone.
- **Algorithm**: parse `.osb` từ đĩa (OsbReader) + assets từ đĩa; với mỗi AnimationVideo trong
  `.osbv`: quyết tập objects do compiler sinh cho video đó — **in-compile verify dùng tagging in-memory (compiler biết
  chính xác object nào nó sinh cho video nào, chính xác tuyệt đối); standalone `verify` subcommand dùng heuristic
  asset-path-prefix của store** (native passthrough objects trỏ asset ngoài store → loại; giới hạn: hai AnimationVideo
  cùng layer chồng thời gian không tách được per-video ở standalone mode → in warning, verify union — chấp nhận,
  standalone chỉ là công cụ debug, authority là stage in-compile). Objects loại khỏi verify = không có ground truth.
  Render composite các object thuộc video tại mọi source-fps timestamp trong window của video, so với frame decode tương
  ứng: per-frame PSNR + TileSsim. Vi phạm floor → ghi record (tMs, metric, tile). Kết thúc: report per scene
  (min/p1/mean) + violations; **artifact luôn được giữ**, exit code ≠ 0 nếu có violation.
- **Golden fixtures**: 2 file `.osb` viết tay trong `tests/fixtures/golden/` — (a) đủ loại command M/MX/S/V/R/F/C/P +
  easing 0/2/3 trên vài sprite, render tại 5 mốc so với ảnh PNG golden committed; (b) positive control: 1 sprite
  full-frame crop pixel-exact từ 1 frame video test → verify phải 100% pass tại `high`.
- **Acceptance**:
    - Negative control: `verify` trên output v1 của bad_apple/fish → có violations (harness bắt được lỗi thật đang tồn
      tại).
    - Positive control (b) 100% pass tại `high` — chống harness "hỏng kiểu fail-everything".
    - Golden (a) khớp pixel (cho phép ±1/255 per channel do rounding).
- **Expected code impact**: `Evaluation/QualityVerifier.cs` mới, `Cli/Commands/VerifyCommand.cs`
  mới, `VideoCompiler` gọi stage, `SoftwareStoryboardRenderer` nhận danh sách object lọc sẵn.

**M0 implementation order**: S0.1 → S0.2 → S0.4 (độc lập, có thể song song) → S0.3 → S0.5 (cần cả S0.1/S0.2).

---

## 4. M0.5 — Preset Calibration Spike

**Purpose**: gỡ vòng lặp "M1 gate trên số M1 tự calibrate" (red-team finding 5). **MUST**. **Dependencies**: M0.

- **Scope**: hidden subcommand `calibrate <video> [--window]`: mô phỏng encoder thô = ErrorMap +
  hard-swap-every-violating-block (không crossfade, không motion — chính là cận trên chi phí của floor). Sweep floor
  PSNR ∈ {30,32,34,36,38,40} × tileSSIM ∈ {0.80,0.85,0.88,0.92}: đo patch count, asset bytes, % diện tích refresh/frame.
  Dump 3 frame PNG reconstruction tại mỗi combo cho visual spot-check.
- **Non-scope**: không phải encoder thật — code này nằm ở `Cli/Commands/CalibrateCommand.cs` + tái dùng ErrorMap khi M1
  viết xong phần đó, HOẶC viết ErrorMap trước trong bước này (ErrorMap là S1.3 kéo lên sớm — chốt: **viết ErrorMap ở
  M0.5**, M1 dùng lại).
- **Deliverable**: bảng floor↔cost per fixture append vào `docs/research.md`; 6 số preset chốt commit vào
  `QualityPreset` + cập nhật plan-v2 §7. User spot-check visual trước khi chốt (đây là điểm dừng chờ review con người
  duy nhất giữa M0 và M1).
- **Acceptance**: bảng + ảnh tồn tại; presets commit; nếu data cho thấy per-frame-min PSNR 34dB bất khả thi trên
  bad_apple/birdbrain (patch rate ≈ NaiveBaseline) → preset dùng tileSSIM làm gate chính + PSNR floor thấp hơn, quyết
  cùng user tại spot-check.

---

## 5. M1 — Closed-Loop Core (P1 patch + P2 crossfade + P5 photometric)

**Purpose**: hết mờ — floor tuyệt đối enforce by construction. **Dependencies**: M0, M0.5. **Non-scope**: motion (M2),
budgets enforcement (M3 — `WorkloadBudgets` tồn tại nhưng
`Allow() => true`), animation (M3), JPEG (M3), multi-target shared decode (N=1, Q6).

### S1.1 `QualityPreset` + CLI — MUST

Record `(PsnrFloorDb, TileSsimFloor, HeadroomDb=2.0, HysteresisFrames=2)`; flag
`--quality high|medium|low` trên `compile` (default high); số từ M0.5.

### S1.2 `ReconstructionState` — MUST

- **Contract**: §2.2. Display list per (scene-segment × target); canvas RGB24 source-res.
- **Algorithm `AdvanceTo(t)`**: giữ danh sách `dynamicObjects` (object có command đang interpolate tại t — fade chạy dở,
  sau này motion track). Union bbox hiện tại + bbox trước của chúng = vùng cần re-composite. Per vùng: clear về đen (osu
  nền đen), rồi vẽ lại MỌI object giao vùng theo declare order (query spatial grid bucket 128px), mỗi object evaluate
  state tại t qua
  `CommandEvaluator` rồi `Compositor.Blit` (bilinear). Object tĩnh ngoài các vùng đó: pixel canvas giữ nguyên (không
  đụng).
- **`Apply(commit)`**: thêm object vào display list + buckets; composite ngay bbox của nó (chỉ nó, đè lên canvas — đúng
  vì nó là declare cuối = top).
- **Invariant nội bộ**: canvas sau bất kỳ chuỗi AdvanceTo/Apply nào == full re-render display list tại t (test đối
  chiếu, xem Tests).
- **`OQ-1`** (perf, không phải correctness): nếu profile M1 cho thấy re-composite vùng động chậm, spike so sánh với
  chiến lược "static base cache layer" — phạm vi an toàn: chỉ đổi nội bộ class này, invariant đối chiếu full-render giữ
  nguyên.
- **Tests**: đối chiếu — sau mỗi 10 AdvanceTo/Apply ngẫu nhiên (property test với document sinh ngẫu nhiên nhỏ),
  canvas == full re-render từ display list (byte-equal). Đây là test quan trọng nhất của M1.

### S1.3 `ErrorMap` — MUST (đã viết ở M0.5)

Per block 16×16: SSD luma → blockPSNR; per tile 64×64: TileSsim (S0.2 dùng lại). Output: bitmap block → {clean, early
(dưới floor+headroom), violating (dưới floor)}. SIMD (`Vector<T>`) cho SSD nếu profile đòi — viết scalar trước, đo.

### S1.4 `DirtyRects` — MUST

- **Algorithm**: từ block bitmap (chỉ violating + early-đã-chín): connected components 4-connectivity → bbox per
  component → nếu bbox fill-ratio < 0.5 và area > 64 blocks: split một lần theo hàng/cột trống lớn nhất → cap mỗi rect ≤
  1024px mỗi chiều (tile nếu vượt) → merge các rect nhỏ (< 32×32px) vào rect kề nếu khoảng cách < 32px (PNG overhead ~
  1.5KB/asset — số đo từ AssetTrimmer, research.md).
- **Tests**: các pattern bitmap cố định → rects mong đợi (5-6 case gồm chữ L, hai đảo, full frame).

### S1.5 Candidate framework — MUST

`ICandidateProvider`/`IRepresentationCandidate`/`EmitContext`/`CommitResult` như §2.2. EmitContext enforce per-property
lastEnd (I5 tại nguồn). Provider list đăng ký trong `ClosedLoopEncoder`
constructor — thứ tự theo `LadderRank`, KHÔNG hardcode kiểu if-chain (I7).

### S1.6 `PatchProvider` — MUST (terminal, I3)

Propose đúng 1 candidate: crop `region` từ `Window.FrameAt(NowMs)`, `SbSprite` origin TopLeft, lệnh `S` scale placement
(đúng cách v1 — mapping qua `CanvasMapping`), span
`[ViolationStartMs, openEnd]` — **open-ended run**: sprite sống tới khi bị patch sau thay (EmitContext vá EndMs của
patch trước khi patch mới cùng vùng commit — bookkeeping per-region
"current patch" trong encoder, truyền qua Commit). RenderPreview = chính crop đó (theo định nghĩa đạt floor tại NowMs
với error ≈ 0; các frame sau nó là "nội dung mới nhất đã biết" — hợp lệ vì frame sau nếu lệch sẽ tự dirty). Frame 0 mỗi
segment: encoder force PatchProvider full-frame (I-frame).

### S1.7 `PhotometricProvider` — MUST

- **Điều kiện propose**: tồn tại sprite top-most phủ ≥90% region (query
  `Reconstruction.QueryRegion`) và region phủ ≥90% bbox sprite đó (C/F là whole-sprite — không tint nửa sprite).
- **Fit**: per channel `c = 255·Σ(A·T)/Σ(A²)` (least squares multiplicative, A=đang hiển thị, T=target), clamp [0,255];
  nếu c xấp xỉ nhau 3 kênh và < 255 → thử thêm biến thể chỉ `F` khi phần dưới sprite là đen (fade-to-black tương đương).
  Emit `C` (hoặc `F`) command span
  `[ViolationStartMs, NowMs]` giữ giá trị từ đó (command sau nối tiếp nếu tiếp tục đổi — lastEnd bookkeeping tự nhiên).
- **Giới hạn ghi rõ**: KHÔNG áp dụng khi có nội dung khác lộ dưới sprite (F làm lộ thứ bên dưới — chỉ dùng F khi vùng
  dưới là nền đen; kiểm bằng QueryRegion). Fail fit/verify → trả rỗng, ladder rơi xuống.
- **Tests**: fixture fade-to-black tổng hợp → verify chọn photometric (đếm asset mới = 0 trong đoạn fade); tint đỏ dần →
  C track.

### S1.8 `CrossfadeProvider` — MUST

- **Propose điều kiện**: có "current patch" A cho vùng này (không phải frame đầu); block không thuộc loại đổi-liên-tục
  (hint: nếu ≥ (HysteresisFrames+2) frame liên tiếp gần nhất vùng này đều violating → trả rỗng, đó là thrash, crossfade
  sai công cụ).
- **Algorithm** (hồi tố, mọi frame trong window — không sample):
  ```text
  B = crop source tại NowMs; A = nội dung đang hiển thị vùng này
  span candidates: dài nhất trước: t_s ∈ {NowMs−w, NowMs−w/2, NowMs−w/4, ...} (≥ 2 frame)
      với mỗi t_s (≥ thời điểm A bắt đầu đúng):
          ∀ frame f ∈ [t_s, NowMs]: err(lerp(A,B,(f−t_s)/(NowMs−t_s)), source_f) đạt floor?
      first pass → emit: sprite B declare sau A, F,0,t_s,NowMs,0,1; A giữ alive tới NowMs
                   (EmitContext vá EndMs của A = NowMs); sau NowMs, B là "current patch"
  none pass → trả rỗng (ladder → PatchProvider)
  ```
  Re-verify quá khứ hợp lệ: các frame (t_s, NowMs) trước đó pass với A tĩnh; composite mới lerp chỉ được nhận khi CŨNG
  pass — và thường pass đẹp hơn (error giảm dần về B).
- **Tests**: `dissolve_synthetic` fixture: gradient A→B chậm → 1 cặp asset thay vì chuỗi patch (assert asset count);
  mid-fade content đổi đột ngột → hard swap (không crossfade chồng).

### S1.9 `ClosedLoopEncoder` — MUST

Vòng §2.2 + hysteresis bất đối xứng (violating hành động ngay; early đếm `HysteresisFrames` rồi thành violating với
`ViolationStartMs` = frame đầu tiên early — backdating); I-frame frame 0; per-region current-patch bookkeeping;
per-frame thứ tự: `AdvanceTo` → `ErrorMap` → `DirtyRects` → ladder → `Apply` → (debug assert: ErrorMap lại vùng vừa sửa
đạt floor).

### S1.10 `FrameWindow` — MUST

Ring buffer `byte[]` frames, cap 2s source-fps *(provisional — đo RAM ở acceptance)*; API
`FrameAt(tMs)`, `Range(t0,t1)`. Frame ngoài window: không truy cập được (crossfade span cap tự nhiên).

### S1.11 `VideoCompiler` wiring + deletions + migration — MUST

- Thay `TileEncodeLoop.RunAsync` call bằng: (per scene-overlap, per member) decode →
  `ClosedLoopEncoder.EncodeSegmentAsync(frames, target, preset)`. `TunedFor`/`ParameterTuner` xóa — `LoopOptions` xóa.
  Baker giữ nguyên hành vi M1 (patch/crossfade sprites vẫn bake group transforms qua `Baker.Bake` như cũ — chưa đụng
  tracked-fold, đó là M2).
- Decode-consumer boundary (Q6): `ClosedLoopEncoder` nhận `IAsyncEnumerable<VideoFrame>` — không biết ffmpeg.
  Multi-member plan: chạy per-member decode tuần tự (regression có chủ đích, ghi CLAUDE.md).
- **Xóa**: `ParameterTuner` + tests, `TileEncodeLoop`, `QuadtreeMerger` + tests, `AnimationDetector`
    + tests, `EncodePipeline`, `NaiveBaseline` (sau khi M0.5 xong), CLI `tune-bench`. **`OQ-6`**:
      hidden subcommands `bench`/`decode`/`probe`/`inspect` phụ thuộc `EncodePipeline` — đề xuất xóa
      `bench`/`probe`, giữ `inspect`/`decode` nếu không phụ thuộc (kiểm lúc làm); **cần user xác nhận trước khi xóa** —
      không tự quyết.
- **Migration/compat giữ**: `.osbv` grammar + native passthrough y nguyên; CLI `compile` signature y nguyên (+
  `--quality`); asset layout `s/{hash}.png`, `a/{hash}/f{n}.png` y nguyên; cross-run asset cache semantics y nguyên.

### M1 Tests/Benchmark/Acceptance (toàn milestone)

- Unit như từng step; integration: compile fixture nhỏ end-to-end → verify stage 0 violations.
- **Acceptance**: (1) 100% frames đạt floor `medium` (số M0.5) trên 5 fixtures — qua verify stage độc lập, không phải
  qua R; (2) `dissolve_synthetic` asset count ≤ 1/5 hard-swap-only (chạy với CrossfadeProvider tắt để có baseline); (3)
  wall time fish_spinning ≤ v1 (tuning+encode) — ghi số thật vào research.md (mục tiêu *(provisional)* ≤4× realtime gồm
  verify; trượt → `OQ-1`
  spike trước khi optimize mù); (4) RAM peak ghi nhận (window 2s + canvas ~ chấp nhận ≤ 2.5GB @1080p — trượt → giảm
  window cap); (5) A/B bảng đầy đủ vs v1 (bytes/sprites/commands/workload/ quality) vào research.md.
- **Failure/fallback tổng**: encoder không bao giờ produce dưới floor (I3); mọi provider lỗi runtime (exception) → log +
  coi như trả rỗng (ladder rơi tiếp) — KHÔNG nuốt exception của PatchProvider (terminal fail = compile fail thật, để lộ
  bug).

**Order**: S1.1 → S1.2+S1.3+S1.4 (song song được) → S1.5 → S1.6 → S1.9 (chạy được với mỗi patch)
→ S1.10 → S1.7, S1.8 (thêm vào ladder) → S1.11 (wire + xóa) → acceptance.

---

## 6. M2 — Motion (K=1 dưới layer-set interface)

**Purpose**: hết nặng trên pan/zoom — nội dung camera-motion ngừng re-emit per frame. **Dependencies**: M1.
**Non-scope**: K>1 layers (DEFER-UNTIL-DATA — M3.5), panorama (DEFER-UNTIL-DATA), rotation camera (bỏ v2.0 — thêm khi
attribution đòi, `R` command sẵn).

### S2.0 Estimation spike — MUST (trước khi build phần còn lại)

Translation-only block matching trên `pan_synthetic` + fish_spinning 5s: đo inlier ratio, residual. Quyết: block
matching đủ / cần pyramidal / cần DIS flow (theo thứ tự đó — learned flow REJECT, research §2.7). Phạm vi an toàn: code
spike ném được, chỉ số liệu vào research.md là deliverable.

### S2.1 `FlowField` — MUST (thuật toán theo kết quả S2.0; mô tả dưới là phương án mặc định)

Downscale luma ≤256px (decode pass A riêng, §2.3). Per block 16×16 (ở scale đó): SAD search radius ±12px, 3-level
pyramid nếu S2.0 đòi. Output: sparse vectors + SAD confidence.

### S2.2 `MotionModelFitter` — MUST

RANSAC similarity không rotation: model `(tx,ty,s)`, 2 điểm/sample, inlier = residual < 1px full-res, iterations 64.
**Interface trả `IReadOnlyList<MotionLayer>`** (v2.0 luôn 0 hoặc 1 phần tử — layer-set từ ngày đầu, I7/M3.5). Accept
gate 2 tầng (plan §6.3): inlier ≥60% trong ≥70% frames VÀ residual-area dự kiến ≤25% frame — reject → list rỗng.

### S2.3 `MotionSegmenter` (Q5) — MUST

Từ per-frame model chuỗi: đoạn `|v|<ε` kéo dài ≥ 500ms → segment static; chuyển tiếp có hysteresis 300ms. Output:
scene → segments `[(t0,t1,MotionLayer?)]`. Encoder chạy per segment (I-frame đầu segment tự nhiên theo S1.9 — chấp nhận,
chính là keyframe tại điểm đổi chế độ).

### S2.4 `TrajectoryFitter` — MUST

RDP joint: điểm = per-frame `(tx,ty,s)`; distance = **max displacement 4 góc frame** của composed transform giữa đường
thẳng nội suy và giá trị thật, tính ở osu!px; epsilon 0.3 *(provisional)*. Output keyframes đồng bộ cho cả 3 kênh.

### S2.5 `TransformTrackProvider` + baker fold — MUST

- Không baker: sprite base full-frame (crop tại keyframe đầu segment), origin Centre, emit
  `MX`/`MY`/`S` keyframed linear từ trajectory (giải `p(t), s(t)`: screen = `p(t) + s(t)·(x−c)`).
- Có baker (**single-writer, I5**): `GroupTransformBaker.BakeTracked(cameraSamples, ...)` — compose bằng **sampling**:
  per source-frame t: group transform tại t (logic `SampleAt` sẵn có) ∘ camera
  `(p(t), s(t))` → chuỗi final state (x,y,sx,sy,α,...) → RDP joint screen-space MỘT LẦN trên kết quả → emit. (Tích 2
  track linear là bậc 2 — không làm đại số piecewise, sample rồi simplify.)
- Base keyframe refresh: khi ErrorMap cho thấy vùng base (không bị patch đè) suy thoái dần tới early-threshold trên >40%
  diện tích → mở keyframe mới (crossfade qua CrossfadeProvider trên full-frame region — tái dùng cơ chế, không code
  riêng).
- Provider rank 20 — propose CHỈ khi `ctx.Motion` có layer phủ region và region chưa có base đúng. Mép lộ do pan
  (coverage gap chủ đích): không thuộc layer → ladder thường (patch) — tự nhiên.
- **4K guard**: nguồn có chiều >3840 → `SceneMotionInfo` trả rỗng (skip motion), lý do vào log (plan §5).

### S2.6 Fixtures — MUST

`pan_synthetic` (ffmpeg zoompan trên ảnh tĩnh lớn), `pan_foreground_synthetic` (pan + sprite di chuyển độc lập chiếm ~
20% frame, thiết kế vừa lọt gate 60/70) — script tạo trong
`tests/fixtures/make_fixtures.ps1`.

### M2 Acceptance

(1) `pan_synthetic` asset bytes ≤ 1/5 M1; (2) minecraft 10s ≤ 60% bytes M1; (3)
`pan_foreground_synthetic`: gate 2 từ chối/nhận đúng theo net-win (kiểm cả hai nhánh bằng chỉnh foreground size); (4)
100% frames vẫn đạt floor (verify độc lập); (5) scene motion-reject → output byte-identical M1.

**Order**: S2.0 → S2.1 → S2.2 → S2.4 → S2.5 (no-baker) → S2.3 → S2.5 (baker fold) → S2.6 → acceptance.

---

## 7. M3 — Budgets + Asset Encoding + Thrash

**Dependencies**: M1 (budgets/encoding), M2 (không bắt buộc cho encoding search — có thể chạy song song nếu cần, nhưng
mặc định tuần tự).

### S3.1 `WorkloadBudgets` enforcement — MUST

- Counters cập nhật tại `Commit` (steady/transient SB load timeline, alive count, animation resident).
  `Allow(candidate)` = kiểm dự phóng sau commit ≤ budgets (§plan-7). Fail → encoder thử candidate/provider tiếp (I6 —
  đổi representation, không hạ floor); PatchProvider bị budget chặn không bao giờ xảy ra với patch time-disjoint (không
  tăng alive dài hạn) — nếu xảy ra (bug/case lạ): commit vẫn thực hiện + ghi violation record (fidelity thắng, I6),
  report cuối.
- **Tests**: budget giả nhỏ → crossfade bị chặn rơi về patch; violation record đúng.

### S3.2 `AnimationProvider` / ThrashPlanner — MUST

- **Post-scene pass** (không nằm trong vòng per-frame): quét lịch sử commit per region; region có ≥
  `minAnimationFrames=4` hard swaps liên tiếp cách nhau đúng 1 frame và tổng span tuân cap (frames ≤120 *(provisional)*,
  area ≤ tile 256px, resident tổng ≤200MB/scene) → gom chuỗi patch thành 1 `SbAnimation` (LoopOnce), xóa các sprite
  patch tương ứng khỏi document (EmitContext hỗ trợ replace — thao tác trên IR trước passes). Uniqueness gate như v1
  (≥0.8 frame distinct — logic cũ của AnimationDetector, đọc từ git history, viết lại gọn).
- **Non-scope**: không animation trong vòng per-frame; không dedupe animation frames (format không cho — [R4]).
- **Tests**: bad_apple 5s: animation resident ≤ budget, floor giữ; vùng flicker A/B/A/B (2 nội dung) KHÔNG thành
  animation (dedupe sprite thắng — uniqueness gate).

### S3.3 Asset encoding search — MUST

- **Spike đầu tiên (`OQ-4`)**: tự tay bỏ 1 `.jpg` vào một storyboard test, load trong osu! stable
    + lazer, xác nhận render. Fail → JPEG bỏ, chỉ dither palette (quyết định matrix cập nhật).
- `AssetStore.GetOrAdd(pixels, w, h, consumer, EncodingPolicy)`: encode candidates {PNG raw, PNG palette 256/128/64 +
  Floyd–Steinberg, JPEG q95/q90/q85 — chỉ khi vùng opaque} → decode lại → `TileSsim` vs gốc ≥
  `preset.TileSsimFloor + margin 0.02` → chọn bytes nhỏ nhất. **Hash key gồm encoding đã chọn** (I10 — nếu không, hai
  policy khác nhau đụng file); extension theo format. Cache quyết định per content-hash (không re-search asset đã gặp).
- **Tests**: gradient → palette bị loại (SSIM gate), photo → JPEG thắng; hash/dedupe vẫn đúng khi 2 policy cùng chạy 1
  compile.

### S3.4 `OsbWriter` value-chaining — MUST

Chuỗi command cùng property, cùng duration bước, back-to-back, cùng easing → 1 dòng nhiều value pairs `[R9]`. Round-trip
test qua OsbReader + OsuParsers validator; verify stage chạy trên file đã shorthand (I2 tự bắt sai lệch semantics nếu
có).

### M3 Acceptance

Bad_apple floor giữ + animation trong budget; JPEG path giảm ≥30% asset bytes trên birdbrain/fish không thủng gate;
không fixture vượt budget mặc định hoặc violation report rõ;
`.osb` minecraft 10s giảm ≥20% nhờ chaining (đo thật).

---

## 8. M3.5 — Attribution Checkpoint

**Purpose**: quyết định research priority tiếp theo bằng số liệu (I9). KHÔNG phải feature milestone.

### S3.5.1 `AttributionAnalyzer` — MUST

Chạy trên compile kết quả của toàn corpus + dữ liệu trung gian encoder (flow, models, commit log — serialize ra
`attribution.json` trong lúc compile khi bật flag `--attribution`):

| Bucket                  | Cách đo (spec đo được, không vibes)                                                                                                                        |
|-------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `local-coherent-motion` | bytes của patches trong vùng mà sequential-RANSAC pass 2 (chạy offline trong analyzer, trên flow đã lưu) tìm được model K=2 giải thích ≥70% blocks vùng đó |
| `pan-reveal`            | bytes của patches nằm trong dải mép ngược hướng camera translation (dải rộng = \|v\|·Δt)                                                                   |
| `perspective-residual`  | bytes patches ở vùng inlier-tâm-nhưng-residual-tăng-theo-bán-kính (fit tuyến tính residual vs khoảng cách tâm, slope > ngưỡng)                             |
| `near-duplicate-assets` | bytes assets có coarse-hash (aHash 8×8) trùng asset khác nhưng exact-hash khác                                                                             |
| `deformation`           | patches còn lại có SSIM(giữa 2 refresh liên tiếp cùng vùng) ≥ 0.7 — đổi có cấu trúc                                                                        |
| `noise`                 | phần còn lại (SSIM refresh liên tiếp < 0.7) — bucket không cứu được                                                                                        |

Output: bảng % bytes per bucket per fixture + tổng corpus, markdown append research.md.

### S3.5.2 Decision process — MUST (process, không phải code)

Bucket lớn nhất ≥25-30% *(provisional)* → mở research/prototype/benchmark cycle cho hướng tương ứng trong menu
(research-representations.md §2: LayeredMotion K>1 / Panorama / CanonicalAssetReuse / PerspectiveStrips) — theo đúng chu
trình: research → prototype → benchmark → keep/reject.
`noise` trội → ship v2, ghi giới hạn vào README. **Không đặt tên M4/M5 trước** — milestone sau M3.5 chỉ tồn tại sau khi
attribution + prototype benchmark thắng.

---

## 9. Open Questions Registry

| ID   | Câu hỏi                                                                        | Vì sao chưa quyết                                     | Evidence thiếu           | Spike quyết định                    | Phạm vi an toàn cho Sonnet                               |
|------|--------------------------------------------------------------------------------|-------------------------------------------------------|--------------------------|-------------------------------------|----------------------------------------------------------|
| OQ-1 | ReconstructionState re-composite chiến lược nào nhanh nhất                     | perf, không phải correctness                          | profile M1 thật          | benchmark 2 chiến lược trên fish 5s | chỉ đổi nội bộ class, giữ test đối chiếu full-render     |
| OQ-2 | Block matching đủ tốt trên real footage?                                       | flow quality content-dependent                        | S2.0 số liệu             | S2.0                                | code spike vứt được, chỉ số liệu là deliverable          |
| OQ-3 | Preset numbers cuối                                                            | content-dependent                                     | M0.5 bảng + visual       | M0.5                                | chờ user spot-check, không tự chốt                       |
| OQ-4 | osu! stable+lazer load `.jpg` storyboard asset?                                | chưa verify thực tế                                   | test thủ công            | đầu S3.3                            | 30 phút thủ công; fail → bỏ JPEG, không workaround       |
| OQ-5 | Alive semantics chính xác (first-alpha>0 vs command span) cho WorkloadAnalyzer | ảnh hưởng độ chính xác report, không ảnh hưởng output | đọc lazer source khi cần | deepwiki câu hỏi cụ thể             | dùng conservative (command span) tới lúc đó              |
| OQ-6 | Số phận `bench`/`decode`/`probe`/`inspect` legacy                              | tùy user còn dùng không                               | ý kiến user              | —                                   | KHÔNG xóa khi chưa được xác nhận; để compile lỗi thì hỏi |

---

## 10. Sau M3.5 (để mở có chủ đích)

Không milestone nào được viết trước ở đây (nguyên tắc XIV). Menu + điều kiện nhận + phân tích khả thi:
research-representations.md §2 và Decision Matrix §8 của file đó. Chu trình bắt buộc cho mọi hướng mới: `attribution → research → prototype (throwaway) → benchmark vs baseline hiện tại →
keep/reject → chỉ khi keep mới viết blueprint section mới`. Blueprint này khi đó được append, không rewrite — nếu một
hướng mới đòi rewrite §2, đó là vi phạm I7 và phải qua Advisor trước.
