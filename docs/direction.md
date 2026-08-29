# OsbMpeg: hướng phát triển tiếp theo

Tài liệu này là nguồn duy nhất cho hướng đi sắp tới của OsbMpeg: chẩn đoán vấn đề của bản cài đặt
hiện tại, những gì osu! storyboard thực sự biểu diễn được, không gian representation, kiến trúc đề
xuất, lộ trình triển khai chi tiết, và các hướng nghiên cứu còn để mở. Nó thay thế toàn bộ tài liệu
research và plan trước đó (`research.md`, `plan-v2.md`, `research-representations.md`,
`blueprint-v2.md`, `research-retrieval.md`); nội dung còn giá trị của chúng đã được gộp vào đây,
phần lịch sử đo đạc nằm ở mục 13.

Người triển khai đọc mục 8 (kiến trúc và invariants) rồi mục 9 (lộ trình từng bước). Gặp điểm tài
liệu không nói rõ và không suy ra được từ invariants: hỏi Advisor, đừng tự phát minh kiến trúc. Các
điểm đánh dấu `OQ-n` (mục 12) là câu hỏi mở đã có sẵn phạm vi spike an toàn.

Ký hiệu phân loại quyết định dùng xuyên suốt:

* `MUST`: đủ bằng chứng, triển khai theo spec.
* `INVESTIGATE`: đáng nghiên cứu, chưa khóa, có phạm vi spike.
* `DEFER`: chỉ quyết sau khi có số liệu được nêu tên.
* `REJECT`: có bằng chứng cho thấy không đáng làm, kèm điều kiện đảo quyết định.

---

## 1. Bản cài đặt hiện tại hỏng ở đâu

Hai triệu chứng: output mờ, và output nặng. Cả hai đều có nguyên nhân xác định được trong code, không
phải chuyện tinh chỉnh tham số.

### 1.1 Vì sao mờ

Sàn chất lượng là số tương đối. `ParameterTuner.cs:55` đặt `TargetSlackDb = 1.0`, sàn được tính bằng
PSNR của combo mặc định trừ đi 1dB. Trên nội dung khó (bad_apple có baseline khoảng 24.9dB) sàn rơi
xuống 23.9dB, nghĩa là ảnh nát vẫn "đạt". Tệ hơn, tuner tối thiểu hóa bytes *xuống sát sàn đó*, nên
chất lượng thấp là mục tiêu tối ưu chứ không phải tai nạn.

Palette quantize không dither. `AssetStore.cs:232` gọi `OctreeQuantizer(MaxColors = Colors)` và tuner
từng chọn `Colors=16` cho short_animation, tạo posterization trên gradient.

Vòng lặp hở. `TileRunTracker` chỉ đóng run khi hash *đã lượng tử hóa* thay đổi, cộng thêm ngân sách
MAE của `TileTolerance`. Thay đổi chậm như dissolve, gradient trôi, hay ánh sáng đổi dần luôn nằm
dưới ngưỡng, nên tile đứng hình cũ vô hạn. Không có gì đo error trên frame thực tế ship ra: PSNR chỉ
được đo lúc tuning, trung bình trên một sample window ngắn, nên staleness cục bộ không bao giờ bị
bắt. `Metrics.Ssim` cũng là SSIM toàn frame, chính doc comment của nó đã ghi rằng một tile sai trong
một frame đẹp gần như không làm số đó nhúc nhích.

### 1.2 Vì sao nặng

Chỉ có đúng một primitive. `TileEncodeLoop.Emit` phát ra mỗi run thành một sprite kèm một lệnh `S`
tĩnh để đặt scale. Không có lệnh động nào (`M`, `F`, `C`, `R`) được dùng để mô tả nội dung. Camera
pan làm mọi tile đổi mỗi frame, và mỗi tile đổi lại phải re-emit ở độ phân giải nguồn. Con số thực
đo được trên Minecraft full clip 60fps: 79.612 sprites, 79.223 assets, 735,01 MB assets cộng 9,31 MB
`.osb`, tổng 744,32 MB, thời gian compile 542 phút.

Animation không dedupe được (format cần chuỗi file đánh số riêng cho mỗi object) và có thể lên tới
300 frame PNG full-res cho một object. Mọi frame đó nằm trong RAM cùng lúc khi osu! load.

Yêu cầu 60fps qua frame duplication nhân đôi mật độ biên run.

### 1.3 Những gì đang đúng và cần giữ

Decode ở độ phân giải nguồn, và `CanvasMapping` khớp đúng phép toán của `DrawableStoryboard`. Đây là
chiến lược sharp duy nhất (xem R2 ở mục 3), nên độ mờ hiện tại không đến từ resolution.

Tầng `OsbMpeg.Parsers` hoàn chỉnh: IR, `OsbWriter` (float, shorthand, loop), `CommandEvaluator` và
`EasingTable` phủ đủ easing 0 đến 34, các pass merge/drop/loop-extract.

`Compositor` có blend semantics đã verify khớp osu-framework: normal blend là straight alpha lerp,
additive là cộng.

Scene detection (`ScenePrePass`, `SceneBounds`) dùng tín hiệu độc lập tham số và đã có unit test.
`AssetStore` content-addressed XXH3-128, chế độ in-memory, dedupe xuyên scene. `FrameSource` streaming
kèm hwaccel, `MediaProbe`, `Metrics.Psnr`.

---

## 2. Đích đến và ràng buộc

Coi OsbMpeg về lâu dài là một compiler đi tìm representation storyboard hiệu quả nhất cho một video,
dưới ràng buộc cứng về chất lượng và về tải renderer.

```text
Video
  ↓
Phân tích scene / thời gian
  ↓
Phân rã thành thành phần
  ↓
Sinh candidate representation
  ↓
Chọn representation
  ↓
Storyboard IR
  ↓
Dựng lại (reconstruction)
  ↓
Kiểm chứng chất lượng và workload
  ↓
Storyboard tối ưu
```

Đây là đích đến, không phải kiến trúc đã khóa. Không kỹ thuật cụ thể nào (object tracking, optical
flow, depth estimation, panorama, RDO) được giả định trước là đáp án.

### 2.1 Ba mục tiêu, đo bằng metric cụ thể

| Mục tiêu | Metric | Cách đo |
| --- | --- | --- |
| Giữ chất lượng | 100% frame đạt PSNR ≥ P và worst-tile SSIM ≥ S theo preset | verify pass: render toàn bộ output ở độ phân giải nguồn, so từng frame với source |
| Gọn nhẹ | asset bytes, `.osb` bytes, asset count | `WorkloadAnalyzer` |
| Không quá tải renderer | peak SB load, peak alive sprites, animation resident bytes, `.osb` line count | `WorkloadAnalyzer` quét timeline |

Khi các mục tiêu xung đột thì fidelity thắng. Ngân sách workload bị vượt: đổi representation (crossfade
dài hơn, merge patch, đổi encoding, keyframe thưa hơn), không bao giờ âm thầm hạ sàn. Vẫn vượt thì in
violation report kèm gợi ý hạ preset và để người dùng quyết.

### 2.2 Hàm mục tiêu và ràng buộc

Thứ cần tối thiểu hóa:

```text
minimize   AssetBytes + OsbBytes
```

Asset bytes trội hơn hẳn trong thực tế (735 MB so với 9,31 MB trên minecraft), nên trọng số giữa hai
số này không thành vấn đề.

Các ràng buộc cứng, không phải trọng số, vì giới hạn renderer là vách đá chứ không phải dốc:

```text
∀ frame:      PSNR ≥ preset.P  và  worst-tile SSIM ≥ preset.S     (fidelity, tối thượng)
∀ t:          SteadySbLoad(t) ≤ 4.0 viewport
              TransientSbLoad(t) ≤ +2.0 (cửa sổ fade)
              AliveSprites(t) ≤ 500
Σ animation:  ResidentBytes ≤ 200MB mỗi scene
∀ asset:      chiều ≤ 1024 (ưu tiên, để giữ atlas), ≤ 3840 (tuyệt đối)
.osb:         ≤ ~1M dòng, ≤ ~10MB mỗi phút video (mềm, chỉ cảnh báo)
```

Không cộng có trọng số các trục workload lại với nhau. Một storyboard "trung bình tốt" nhưng vượt
fill-rate ở một đoạn vẫn lag đúng ở đoạn đó, và peak mới là thứ người chơi cảm nhận.

### 2.3 Quality preset

Gate gồm PSNR tối thiểu mỗi frame và worst-tile SSIM (tile 64×64 luma). Global SSIM chỉ in trong
report, không gate, vì nó là aggregate kiểu trung bình, đúng loại mù cục bộ đã gây ra vấn đề ở 1.1.

| Preset | PSNR tối thiểu mỗi frame | Worst-tile SSIM |
| --- | --- | --- |
| `high` (mặc định) | 38 dB | 0,92 |
| `medium` | 34 dB | 0,88 |
| `low` | 30 dB | 0,80 |

Các số này là placeholder và được chốt bởi spike calibration M0.5 (mục 9.2), không phải đoán tay. Dữ
liệu lịch sử cảnh báo trước: bad_apple có baseline khoảng 24,9dB *trung bình*, nên PSNR *tối thiểu*
34dB trên nội dung nhiễu có thể đòi patch rate gần bằng full refresh. Spike sẽ đo đường cong sàn so
với patch rate trên từng fixture rồi đặt số, kể cả khả năng preset dùng tileSSIM làm gate chính và
PSNR thấp hơn nhiều so với trực giác.

CLI: `compile <input.osbv> <output.osb> <assets-dir> [--hwaccel MODE] [--quality high|medium|low]`.

---

## 3. osu! storyboard thực sự biểu diễn được gì

Mười sự kiện quyết định thiết kế, trích từ osu!wiki cùng source `ppy/osu` và `ppy/osu-framework`.
Encoder bắt buộc tôn trọng.

**R1. Command nội suy liên tục.** Mỗi command trở thành một `Transform` của osu!framework, được đánh
giá như hàm liên tục của thời gian trong mỗi `Update()`, tại refresh rate thực của màn hình. Chuyển
động qua `M` hay `S` không cần một command mỗi frame; vài keyframe linear là mượt tuyệt đối, mượt hơn
cả fps của nguồn.

**R2. Scale là float tự do, lọc bilinear kèm mipmap (tối đa 3 mức).** Phát asset ở độ phân giải nguồn
rồi scale xuống không gian 640 cho ra hình sắc nét trên mọi màn hình. Tỷ lệ từ storyboard-space sang
screen là `screen_height/480`, tức 2,25× ở 1080p, 3× ở 1440p, 4,5× ở 2160p. Việc author asset ở độ
phân giải 640 rồi để osu! phóng lên mới là nguyên nhân mờ, không phải giới hạn của renderer.

**R3. Mô hình chi phí của renderer.** Chi phí mỗi frame xấp xỉ fill-rate và overdraw (diện tích các
sprite đang sống; texel trong suốt bên trong một quad opaque-alpha vẫn tốn) nhân với số texture bind.
Sprite nằm ngoài lifetime tốn gần như bằng không vì bị bỏ qua cả `Update` lẫn `Draw`. Sprite alpha=0
giữa lifetime thì vẫn sống và vẫn phải duyệt transform. Tổng số sprite và command trên toàn timeline
chủ yếu là chi phí parse và memory. Thiết kế time-disjoint một sprite mỗi vùng đã tối ưu chi phí vẽ.

**R4. Frame của Animation được preload toàn bộ, không stream.** N frame nghĩa là N texture nằm trong
memory cùng lúc. Frame count của Animation là trục đắt thật sự về memory, khác hẳn command count.

**R5. Command cùng property chồng thời gian được stable và lazer xử lý khác nhau, vĩnh viễn**
(ppy/osu#7257, maintainer xác nhận đây là thiết kế có chủ đích của lazer chứ không phải bug cần sửa
cho khớp stable). Mọi track theo property phải rời nhau (`next.start == prev.end`). Property thật sự
là X, Y, ScaleX, ScaleY, Rotation, Alpha, Colour, Blending, các flip. `S` và `V` đụng nhau trên
Scale, `M` đụng `MX` và `MY`.

**R6. Atlas 1024×1024.** Asset từ 1024² trở xuống (trừ padding) được gộp chung atlas và batch được.
Lớn hơn vẫn render tới `MaxTextureSize` khoảng 4096 nhưng mất batching. Ranking criteria giới hạn
17 triệu px² mỗi ảnh.

**R7. Lifetime.** `StoryboardSprite.StartTime` tự suy ra từ điểm alpha lớn hơn 0 đầu tiên, nên đệm
alpha=0 ở đầu là miễn phí. Không có cơ chế đối xứng ở cuối, nên encoder phải kết thúc mọi sprite bằng
command chấm dứt đúng thời điểm nó hết nhiệm vụ.

**R8. Widescreen là key `WidescreenStoryboard: 1` trong file `.osu`, không phải trong `.osb`.** Một
compiler chỉ phát `.osb` không thể tự bật canvas rộng 853,33. Đây là yêu cầu đóng gói, phải ghi rõ
trong README và trong output note.

**R9. Không bao giờ phát `[Variables]`.** Một case thật với 554 biến biến thời gian load từ khoảng 3
giây thành 45 giây. Ưu tiên value-chaining shorthand: một dòng command với nhiều cặp giá trị tự expand
thành chuỗi bước đều.

**R10. Quy mô đã được chứng minh.** Một storyboard 34.847 sprite, 977 nghìn dòng, `.osb` 27 MB vẫn
load khoảng 3 giây và chạy được. Không có bức tường về số sprite; hiệu năng suy giảm mềm. `.osb` trên
10 MB bắt đầu có nhược điểm.

---

## 4. Không gian representation

### 4.1 Danh mục những gì storyboard biểu diễn được

| # | Representation | Cấu tạo | Bytes | Workload | Thắng trên |
| --- | --- | --- | --- | --- | --- |
| T1 | StaticPatch | 1 sprite, asset crop, span [t0,t1) | 1 asset | 1 alive, fill = diện tích | nội dung tĩnh |
| T2 | PatchSequence | chuỗi T1 rời nhau cùng vùng | N assets | 1 alive | fallback vạn năng (chính là bản hiện tại) |
| T3 | Crossfade | 2 sprite chồng, `F` 0→1 trên cái mới | 2 assets mỗi cặp | 2 alive trong fade | thay đổi chậm: dissolve, ánh sáng, biến dạng chậm |
| T4 | TransformTrack | 1 sprite kèm keyframe `MX`/`MY`/`S`/`V`/`R` | 1 asset cộng ít text | 1 alive | chuyển động rigid, nội suy liên tục theo R1 |
| T5 | MotionLayerSet | K sprite T4, xếp theo z | K assets | K alive | parallax, tách foreground khỏi nền |
| T6 | Panorama | asset lớn cắt thành strip, chung một track M | xấp xỉ hợp nội dung | strip alive, fill chỉ phần trên màn | camera pan hoặc scroll dài |
| T7 | Animation | flipbook, frameDelay | N frame, không dedupe | N texture resident (R4) | thrash thật sự |
| T8 | Photometric | `F` hoặc `C` trên sprite sẵn có | gần 0 | 0 mới | fade in/out, tint, đổi sáng cả vùng |
| T9 | AdditiveLayer | sprite kèm `P,A` | 1 asset | +1 alive | glow, flash, lửa; thứ duy nhất làm sáng thêm được vì `C` chỉ nhân tối đi |
| T10 | SpriteReuse | 1 asset, nhiều instance khác transform và thời gian | 1 asset | bằng số instance | motif lặp: particle giống nhau, UI, texture lặp |
| T11 | SolidColor | asset đơn sắc nhỏ scale to | gần 0 | fill theo vùng | letterbox, fade to black, nền phẳng |
| T12 | AlphaMatteSprite | PNG có alpha, mép feather | 1 asset | fill tính cả quad, kể cả texel trong suốt (R3) | mép mềm cho foreground layer, bắt buộc để layer không lộ viền chữ nhật |
| T13 | FlipReuse | `P,H` hoặc `P,V` | 0 | 0 | nội dung đối xứng, hiếm nhưng gần như miễn phí để thử |

### 4.2 Giới hạn cứng của không gian này

Phần tuyến tính của transform trong osu!framework là `R(θ)·diag(sx,sy)`, tức rotation sau scale, một
lần. Đó là 5 tham số và **thiếu shear** so với affine 6 tham số (phân tích SVD `A = R1·Σ·R2` cần
rotation cả trước lẫn sau scale). Hệ quả:

* Không có shear, không có perspective hay homography cho một sprite. Cách xấp xỉ là chia thành các
  strip, mỗi strip một similarity. Chi phí tuyến tính theo số strip, sai số cỡ bình phương chiều cao
  strip nhân độ cong của perspective.
* Không mask hay clip tùy ý. Wipe transition may mắn biểu diễn được bằng sprite của scene B trượt vào
  với mép cứng, nhưng iris hay wipe theo hình thì không.
* `C` là tint đều cho cả sprite, không có gradient tint. Gradient phải nằm sẵn trong asset.
* Không có primitive blur. Đổi focus phải crossfade sang asset đã blur sẵn, tức tốn thêm asset.
* Không có particle system. Mỗi hạt là một sprite; T10 giúp tiết kiệm asset chứ không giúp sprite count.
* `C` chỉ nhân nên chỉ làm tối. Muốn sáng hơn ảnh gốc phải dùng T9.
* Alpha theo từng pixel bên trong asset thì có (PNG), nhưng fill-rate vẫn tính cả texel trong suốt
  theo R3, nên rect phải bó sát và feather phải mỏng.

### 4.3 Các tổ hợp đáng chú ý

T3 lồng T4 (crossfade giữa hai TransformTrack) xấp xỉ được "ngoại hình đổi trong khi đang di chuyển",
tức morph thô. Ca này quan trọng vì biến dạng chậm cộng chuyển động là chuyện thường gặp, ví dụ người
đi bộ ở xa camera.

T6 làm layer đáy cho T5 phủ được ca pan kèm foreground độc lập. T8 áp lên mọi thứ nên fade cả scene
không tốn asset nào. T2 là tầng đáy phủ mọi sai số của các tầng trên, và chính điều đó biến mọi tầng
trên thành optimization thuần: chúng sai thì tốn thêm chứ không tạo artifact.

---

## 5. Chiến lược phân rã và tối ưu

### 5.1 Khung thống nhất: layered similarity motion

Một khung duy nhất bao trùm gần hết các hướng thường được đề xuất. Bài toán: gán mỗi block 16×16 của
mỗi frame vào một trong K motion model similarity, cộng phần dư. Thuật toán cổ điển, không cần ML:

```text
flow theo block (block matching pyramidal)
   → sequential RANSAC: fit model trội nhất, loại inlier, fit tiếp (K ≤ 4)
   → lọc theo tính liên thông không gian: layer phải là blob liền ≥ diện tích tối thiểu
   → thời gian: gán của frame trước làm prior cho frame sau
```

K=1 chính là global camera motion. "Entity" là layer có support nhỏ. Parallax là các layer khác biên
độ translation. Panorama là layer tích lũy theo thời gian. Occlusion là hệ quả của z-order giữa các
layer. Nghĩa là không phải chọn giữa object tracking, motion layers hay depth: chúng là một trục K và
một trục lifetime của cùng cơ chế. Vì vậy phần motion phải được xây dưới interface layer-set ngay từ
đầu, dù bản đầu chỉ dùng K=1.

### 5.2 So sánh các chiến lược phân rã

| Chiến lược | Thắng trên | Chi phí compute | Rủi ro | Kết luận |
| --- | --- | --- | --- | --- |
| Global motion (K=1) | pan và zoom camera, chiếm đa số footage game và cinematic | thấp | có gate hai tầng | MUST (M2) |
| Motion layers (K≤4) | parallax, tách foreground | trung bình | layer phân mảnh do noise; mép layer cần alpha matte T12 | DEFER tới checkpoint |
| Object tracking ngữ nghĩa | không rõ | cao | output là mask hoặc box, storyboard chỉ tiêu được rect kèm similarity | REJECT (5.6) |
| Depth estimation | thứ tự z cho layer | cao | z-order suy được từ chính occlusion của motion layers | REJECT cho core |
| Panorama, mosaic | pan dài, scroll | trung bình | mover nhiễm vào stitch (median cộng mask xử lý được); giới hạn texture; parallax trong nền phá stitch | DEFER tới checkpoint |
| Suy luận occlusion | không rõ | gần 0 | không | byproduct, không phải feature (5.3) |
| Photometric | fade, flash | gần 0 | ít | MUST (M1) |
| Residual patches | mọi thứ còn lại | không rõ | không | tầng đáy vĩnh viễn |

### 5.3 Occlusion là hệ quả, không phải feature

Với layers, z-order và closed loop, mọi yêu cầu về occlusion tự thỏa. "A che B nên B không cần update"
đúng theo nghĩa đen: closed loop đo error trên composite, vùng B bị che không đóng góp error nên
không patch nào được chi. "A biến mất, B vẫn tồn tại": sprite B có lifetime dài, và nội dung vùng vừa
lộ ra được verify ngay tại frame nó lộ, vì `ErrorMap` chạy sau `AdvanceTo`. Nội dung đúng thì tốn 0,
chưa từng thấy thì patch, đó là điều đúng phải làm.

Thứ duy nhất cần chủ động là kéo dài lifetime của layer bị che thay vì đóng nó, tức giữ sprite sống
qua occlusion nếu dự đoán sẽ lộ lại và nội dung còn đúng. Đó là một heuristic nhỏ trong bộ chọn
representation, không phải một hệ thống.

### 5.4 Định danh entity chuyển thành tái dùng asset chuẩn hóa

Lợi ích thật của "cùng một entity" là tái dùng asset, và có đường rẻ hơn tracking nhiều. `AssetStore`
content-hash đã là re-identification miễn phí cho nội dung trùng byte, xuyên scene và xuyên file. Mở
rộng tự nhiên là chuẩn hóa trước khi hash: đưa patch về pose chuẩn (scale về bậc lượng tử hóa, khử
rotation nếu dùng) rồi hash, còn instance thì đặt lại bằng `M`, `S`, `R` của sprite. Cùng một ngôi sao
ở 5 vị trí và 3 cỡ trở thành 1 asset kèm 5 instance, mà vẫn là exact-hash nên không cần fuzzy search.

Near-duplicate thật sự thì tra ứng viên bằng coarse hash rồi verify bằng SSIM so với sàn. Cách này
bounded và an toàn với sàn cứng, nhưng để dành tới checkpoint vì chỉ đáng khi attribution chỉ ra
bucket này lớn.

### 5.5 Panorama: số học khả thi

Pan 10 giây với 100 osu!px mỗi giây trên nền 1080p cho panorama khoảng 2920×1080. Vượt 1024 nên mất
atlas, nhưng vẫn dưới 4096. Cắt thành strip rộng tối đa 1024, tất cả strip chung một trajectory để
breakpoint đồng bộ. Strip ngoài màn bị GPU clip nên fill gần 0, chỉ tốn một ít alive count. Pan dài
tùy ý chỉ là thêm strip, không chạm `MaxTextureSize`.

Stitch bằng cách warp các frame về hệ panorama qua chính global transform đã có, tích lũy median theo
từng pixel để loại mover, kèm confidence mask. Pixel chưa từng thấy để trong suốt và closed loop sẽ
patch khi nó lộ. Asset bytes xấp xỉ hợp nội dung, tức cận dưới lý thuyết của mọi cách encode pan.
Điều kiện nhận: scene đã accept K=1 và residual thấp, nghĩa là nền thực sự rigid.

### 5.6 Vì sao không dùng semantic detection và learned model

Lý do là lệch độ phân giải quyết định. SAM, RAFT hay depth-anything cho mask, flow và depth chính xác
tới từng pixel, nhưng storyboard chỉ tiêu được rect kèm similarity kèm alpha. Phần chính xác vượt mức
rect là thông tin bị vứt đi, trong khi giá phải trả là dependency ONNX runtime, model hàng trăm MB,
inference mỗi frame và GPU. Block-level classical (block matching cộng RANSAC) khớp đúng độ phân giải
mà representation quyết định được.

Cửa quay lại, ghi rõ để khỏi tranh cãi lại từ đầu: nếu checkpoint cho thấy motion layers fail chủ yếu
vì chất lượng flow trên nội dung khó, thử DIS flow (classical, nhẹ) trước, learned flow sau cùng.

Tương tự, "camera cộng 4 motion layer cộng patches" thắng "camera cộng 37 object ngữ nghĩa", vì 37
object vẫn phải nắn về rect similarity trước khi emit, và khi đó chúng thành 37 layer chất lượng thấp
hơn 4 layer fit thẳng trên flow.

### 5.7 Bốn tầng tối ưu

Không gian tìm kiếm phân rã tự nhiên thành bốn tầng gần độc lập:

```text
(1) Phân rã mỗi scene:        chọn tập layer (K model, có panorama không, ranh giới thời gian)
(2) Representation mỗi vùng:  chọn T1..T13 cho từng dirty region
(3) Keyframe mỗi track:       nén trajectory và span crossfade
(4) Encoding mỗi asset:       PNG, PNG palette có dither, JPEG
```

| Tầng | Thuật toán | Vì sao không cần nặng hơn |
| --- | --- | --- |
| (1) | matching pursuit: thêm layer hoặc panorama khi lợi ích biên đo được lớn hơn chi phí biên | số ứng viên nhỏ (K ≤ 4, panorama có hoặc không); lợi ích đo rẻ trên bản downscale; sai thì closed loop trả bằng patch chứ không bằng artifact |
| (2) | ladder: thử từ rẻ tới đắt, nhận cái đầu tiên đạt sàn | fidelity là ràng buộc cứng nên bài toán là min cost có ràng buộc, không phải đánh đổi theo λ; chi phí các bậc cách nhau hàng bậc độ lớn nên greedy xấp xỉ tối ưu |
| (3) | RDP joint trong screen space cho track; mở rộng greedy cho span crossfade | error đơn điệu theo span nên greedy tối ưu |
| (4) | thử các encoding, verify SSIM, chọn bytes nhỏ nhất | độc lập hoàn toàn theo từng asset nên exhaustive là đúng nghĩa đen |

Bác bỏ (đến khi đo được greedy bỏ phí hơn 10 đến 15% trên corpus): global graph optimization, beam
search, DP toàn cục, RDO quét λ. Lý do chung: interaction giữa các quyết định đã bị chặn bởi kiến
trúc phân tầng, vì tầng patch đáy hấp thụ mọi hệ quả xấu thành *chi phí đo được*, nghĩa là greedy
nhìn thấy giá thật của mình ngay. Còn mọi phương pháp toàn cục đều cần cost model *dự đoán* thay vì
*đo*, và đó là nguồn sai mới. Bài học có sẵn: spike heatmap partitioning từng cho ước lượng đẹp nhưng
đo thật thua 2,6 đến 5,4 lần (mục 13.4).

Closed loop trong bức tranh này là cơ chế đúng đắn và là máy đo chi phí thật cho mọi tầng trên. Trí
tuệ nằm ở bốn tầng; closed loop bảo đảm mọi trí tuệ đó không thể sai thành artifact, chỉ có thể sai
thành tốn.

---

## 6. Góc nhìn synthesis: truy hồi storyboard ngược

Có một cách nhìn khác về cùng kiến trúc: coi video đầu vào như thể nó từng được sinh ra bởi một
storyboard, và nhiệm vụ là truy hồi một chương trình storyboard giải thích được nó với chi phí thấp.
Góc nhìn này không thay kiến trúc; nó đặt tên đúng cho kiến trúc đang có và chỉ ra chỗ còn nghèo.

### 6.1 Encoder đã là một synthesizer

Vòng lặp closed-loop chính là một thể hiện của CEGIS (counterexample-guided inductive synthesis):
verify tìm phản ví dụ (dirty region), synthesizer đề xuất fix cục bộ (candidate), rồi verify lại.
Storyboard là một DSL:

```text
Program   := Object*                       (compose theo painter's algorithm: layer rồi thứ tự khai báo)
Object    := Sprite(asset, origin, x, y, Track*) | Animation(assets[], frameDelay, loopType, Track*)
Track     := thuộc 1 trong 9 property độc lập; mỗi track là chuỗi segment không chồng (R5)
Segment   := (t0, t1, v0, v1, easing ∈ 0..34)
Loop      := đường cú pháp, flatten được; Trigger nằm ngoài phạm vi vì phụ thuộc gameplay
```

Ba tính chất quyết định chiến lược. Thứ nhất, semantics đã executable: `render(Program, t)` được cài
hai lần độc lập trong repo (một bản nhanh cho encoder, một bản authority cho verify), và đó là điều
kiện tiên quyết của mọi synthesis-by-verification. Thứ hai, compose theo z là compositional: thêm
object chỉ ảnh hưởng vùng nó phủ, nên synthesis cục bộ hợp lệ. Thứ ba, mỗi track có số chiều thấp, nên
fit một segment là bài curve-fitting cổ điển và rẻ.

Nhưng không có identifiability toàn cục: vô số chương trình render ra cùng một video. Vì vậy "truy hồi
đúng chương trình gốc" là mục tiêu sai; "một chương trình tương đương trong sai số ε với chi phí nhỏ
nhất" mới là mục tiêu đúng, và đó chính là hàm mục tiêu ở 2.2.

### 6.2 Quan sát ánh xạ sang giả thuyết nào

| Chữ ký quan sát được (đo rẻ) | Giả thuyết đáng fit, theo thứ tự | Đã có provider? |
| --- | --- | --- |
| Δ mỗi pixel xấp xỉ 0 | tĩnh, không làm gì | có (không dirty) |
| Δ là phép nhân đều theo kênh trên cả sprite | `C` tint; `F` nếu nền dưới đen | Photometric |
| Δ là blend đơn điệu giữa hai ảnh ổn định | crossfade | Crossfade |
| flow đồng nhất một model similarity | track `M`, `S`, có thể `R` | TransformTrack (M2) |
| flow tách thành K model theo không gian | multi-layer (T5) | để dành tới checkpoint |
| track 1D có gia tốc hoặc giảm tốc mượt | segment easing thay nhiều keyframe linear | chưa có, xem 6.3 |
| có chu kỳ lặp (autocorrelation cao) | `L` loop; Animation LoopForever | một phần, qua LoopExtractor hậu kỳ |
| nội dung trùng nội dung đã thấy | tái dùng sprite hoặc asset kèm transform | để dành tới checkpoint |
| đổi mỗi frame, không có cấu trúc | Animation hoặc chuỗi patch | Patch, Animation (M3) |
| vùng bị che rồi lộ lại không đổi | kéo dài lifetime sprite dưới | hệ quả của closed loop |

Về z-order: nó chỉ quan sát được tại các sự kiện occlusion, tức khi hai layer giao nhau và một cái
thắng. Không có occlusion thì không suy ra được, nhưng cũng vô hại vì mọi thứ tự đều render đúng. Câu
trả lời đúng là truy hồi *một* thứ tự z nhất quán với mọi sự kiện occlusion quan sát được, bằng một
đồ thị ràng buộc nhỏ và topological sort. Có chu trình là bằng chứng phân rã sai, và khi đó phải hạ
giả thuyết xuống.

### 6.3 Fit hàm, không chỉ fit easing

Nguyên tắc rộng hơn: bất cứ biến đổi nào mô tả được bằng một hàm theo thời gian đều nên được xét dưới
dạng fit đường cong thay vì phát từng frame. Easing chỉ là một trường hợp.

Cơ chế cụ thể: RDP hiện tại chỉ biết segment linear. Mở rộng nó thành, với mỗi đoạn giữa hai
breakpoint ứng viên, thử fit *một* segment easing trước khi chấp nhận chèn thêm keyframe. Việc fit là
đánh giá một số hàm cố định trên track vô hướng, không có tham số tự do nào ngoài (v0, v1), nên rất
rẻ và kiểm tra residual có dạng đóng. Segment easing đạt epsilon thì một command thay được N.

Tập easing đáng thử không cần cả 35: chín hàm gồm Linear, In/Out/InOut Quad, In/Out/InOut Cubic,
In/Out Sine đã phủ hầu hết chuyển động được author. Elastic, Bounce và Back chỉ khớp motion graphics
rất đặc thù, thêm sau nếu dataset chỉ ra.

Thắng lớn trên hai loại nội dung: nội dung vốn được author bằng tween (storyboard thật, motion
graphics, UI game) vì khi đó khớp chính xác và N keyframe thu về 1 command; và gia tốc vật lý mượt vì
ease xấp xỉ hàm bậc hai còn RDP linear phải chèn dày ở đoạn cong. Vô ích trên trajectory nhiễu hay
giật, ví dụ footage cầm tay, vì ở đó keyframe linear mới đúng.

Áp cho mọi track fit được: x, y, scale, alpha, colour. Track alpha và colour hiện sinh từ provider
Photometric và Crossfade, nên dùng chung một fitter.

### 6.4 Chọn candidate đầu tiên hay candidate tốt nhất

Hiện tại ladder chọn candidate *đầu tiên* đạt sàn. Câu hỏi mở là có nên đánh giá mọi candidate đạt sàn
rồi chọn cái rẻ nhất. Phân tích: chi phí các bậc cách nhau hàng bậc độ lớn (photometric gần 0, track
vài KB, crossfade 2 asset, patch 1 asset, animation N asset), nên chọn cái đầu tiên xấp xỉ tối ưu ở
ranh giới giữa các bậc. Chỗ có thể sai nằm trong cùng một bậc và giữa hai bậc kề nhau: crossfade dài
so với hai patch, animation so với chuỗi patch, một command easing so với ba keyframe. Chênh lệch nhỏ,
nhưng tần suất thì chưa biết.

Cách biến câu hỏi thành số: chạy một chế độ đo trong đó mỗi region đánh giá mọi provider mà không dừng
sớm, rồi log cặp (ladder chọn gì, rẻ nhất là gì, chênh bao nhiêu bytes). Tổng chênh dưới khoảng 10%
thì giữ ladder và đóng câu hỏi bằng số; từ 10% trở lên thì nâng bộ chọn thành best-among-passing, một
thay đổi cục bộ trong vòng chọn, không đụng contract của provider.

### 6.5 Dataset có ground truth và metric tầng hai

Đây là chỗ duy nhất có ground truth ở mức *chương trình* chứ không chỉ mức pixel, và nó rẻ để xây vì
generator lẫn renderer đều đã có trong repo.

```text
tools/OsbMpeg.DatasetGen/            (project console riêng, không đụng Compiler)
dataset/case_XXX/
├── source.osb + assets/             sinh bởi generator theo template mỗi category, có seed
├── frames/ *.png                    render bằng SoftwareStoryboardRenderer (tier rẻ, lossless)
├── rendered.mp4                     ffmpeg từ frames, x264 CRF 16 (tier thực tế, vì nhiễu codec
│                                    cũng là một phần bài toán thật)
└── metadata.json                    category, seed, resolution, fps, duration, số sprite, số
                                     animation, số command, asset bytes, số layer
```

Các category, mỗi cái một template có tham số ngẫu nhiên: tĩnh, một transform đơn (`M`, `S`, `R` từng
cái), từng họ easing, fade, colour animation, crossfade, pan, zoom, nhiều sprite, layered motion kèm
occlusion, animation flipbook, loop, hỗn hợp, và một nhóm mập mờ gồm những case mà cùng một video sinh
ra được từ hai chương trình khác nhau trở lên.

Vấn đề vòng tròn phải ghi thẳng: tier rẻ render bằng chính renderer của mình, nên bug renderer nhiễm
cả dataset lẫn kết quả truy hồi. Giảm rủi ro bằng hai cách: golden test ở M0 đã ghim renderer so với
semantics của osu!, và một tier vàng gồm 5 đến 10 case render bằng osu! lazer thật (cách làm cụ thể
còn phải xác định lúc build). Sai lệch giữa tier rẻ và tier vàng trên cùng một case chính là bug
renderer, và bản thân nó đã là phát hiện có giá trị.

Không giả định truy hồi luôn khả thi. Nhóm case mập mờ và các case dùng đặc tính chưa hỗ trợ tồn tại
để đo giới hạn, không phải để pass 100%.

Về metric, có ba tầng với thẩm quyền rõ ràng: so sánh văn bản thô là vô nghĩa và chỉ dùng để debug;
so sánh cấu trúc chỉ có ý nghĩa sau khi chuẩn hóa và chỉ dùng cho research; tương đương khi render
(mọi frame, mọi tile đạt sàn) mới là thẩm quyền. Khi hai storyboard khác nhau nhưng render tương
đương thì theo mục tiêu sản phẩm đó là thành công, và metric tầng hai lúc ấy chỉ còn để hỏi cách của
ground truth có rẻ hơn cách đã truy hồi không.

Những thứ không suy ra được từ pixel, đừng đo đúng sai trên chúng: z-order ngoài các sự kiện occlusion,
phân chia giữa scale của asset và scale của command, phân rã khi các layer không bao giờ tách chuyển
động, và keyframe dày so với easing (render trùng nhau). Những thứ đo được có nghĩa: trajectory
composite theo vùng màn hình, nội dung asset sau khi chuẩn hóa, tập sự kiện occlusion, và độ phức tạp.

| Metric | Cách đo | Trả lời câu hỏi |
| --- | --- | --- |
| `complexity_ratio` | cost(truy hồi) chia cost(ground truth) theo từng trục | generator tốt tới đâu |
| `representation_confusion` | ghép tương ứng vùng theo thời gian (IoU screen-space ≥ 0,5, bipartite match) rồi lập ma trận primitive của ground truth so với primitive đã chọn | chọn sai ở đâu |
| `missing_primitive_attribution` | bytes ở vùng mà ground truth dùng primitive X còn ta dùng patch hoặc animation | còn thiếu primitive nào |
| `trajectory_rmse` | RMSE screen-space của trajectory composite trên các vùng đã ghép | chất lượng fit |
| `asset_recall` | tỷ lệ asset của ground truth có match (exact hoặc canonical hash) | tái dùng có hoạt động không |
| `occlusion_consistency` | tỷ lệ sự kiện occlusion tái tạo đúng thứ tự thắng | z-order |

Giữ các metric này riêng biệt. Không xây score tổng hợp cho tới khi có dữ liệu về tương quan, và không
metric tầng hai nào được làm gate.

---

## 7. Failure modes

| Nội dung | Cơ chế fail | Representation đỡ được | Worst case chấp nhận |
| --- | --- | --- | --- |
| Hard cut | mọi model chết cùng lúc | scene detection sẵn có, cộng I-frame | một asset full-frame mỗi cut, đúng giá |
| Chuyển động nhanh | flow không bắt kịp, crossfade sai công cụ | T7 animation có cap, hoặc T2 với patch rate cao | gần bằng full refresh cục bộ, sàn vẫn giữ |
| Camera xoay trong mặt phẳng | không | T4 có `R` | ổn |
| Perspective, xoay ngoài mặt phẳng | similarity thiếu shear | chia strip, hoặc T2 | patch trả, đo attribution rồi mới xây strip |
| Biến dạng chậm (mặt, vải) | không primitive nào biến dạng | T3 crossfade, tốt bất ngờ vì nội suy tuyến tính giữa hai pose xấp xỉ morph khi Δ nhỏ | T2 khi nhanh |
| Particle, khói, lửa | hàng trăm mover nhỏ | T10 khi các hạt giống nhau, cộng T9 vì lửa và glow vốn là additive | T7 hoặc T2 kèm cap ngân sách |
| Phản chiếu, specular | phi rigid và phi photometric | không | patch |
| Đổi ánh sáng toàn cục | không | T8, hoặc T9 khi cần sáng thêm | gần như miễn phí |
| Dissolve | không | T3 native | gần như miễn phí |
| Wipe | không | sprite của scene B kèm track M, mép cứng | T2 nếu wipe theo hình lạ |
| Nhiễu và grain | phá đồng thời motion coherence, dedupe và crossfade | không hướng nào cứu được | JPEG asset cộng preset thấp hơn |
| Occlusion | không | hệ quả của kiến trúc (5.3) | không |

Nhiễu là ranh giới vật lý của toàn bộ cách tiếp cận này. Dữ liệu cũ đã cho thấy: tuner bó tay trên
birdbrain, còn bad_apple ra hơn 10 nghìn sprite cho 5 giây. Nói rõ một lần trong tài liệu sản phẩm,
đừng để mỗi milestone lại "phát hiện" lại.

---

## 8. Kiến trúc

### 8.1 Invariants

Mọi code, hiện tại và tương lai, phải giữ. Vi phạm là bug kiến trúc, không phải lựa chọn phong cách.

**I1.** IR là ngôn ngữ chung duy nhất. Mọi representation, hiện có hay thêm sau, phát ra `SbSprite`
hoặc `SbAnimation` cùng command chuẩn vào `SbDocument`. Không có kênh phụ, không có "gợi ý cho
renderer" nằm ngoài IR. Reconstruction và Verification chỉ hiểu IR.

**I2.** Verification độc lập là thẩm quyền cuối. Verify pass parse lại `.osb` từ đĩa, đọc asset từ
đĩa, và render bằng `SoftwareStoryboardRenderer`; nó không dùng `ReconstructionState` in-memory của
encoder. Dùng chung thư viện tầng thấp (`Compositor`, `CommandEvaluator`) thì được, khác nhau ở
orchestration và nguồn dữ liệu.

**I3.** Chuỗi fallback luôn kết thúc ở PatchProvider, và provider cuối này không bao giờ fail vì nó
chỉ crop từ frame nguồn. Mọi optimization fail thì rơi xuống bậc dưới, cùng lắm là patch. Đắt thì
được, sai thì không.

**I4.** Confidence và heuristic không bao giờ thay pixel check. Motion confidence, hash equality, RDP
error, tracking score chỉ dùng để *đề xuất* candidate. Mọi commit phải qua pixel-level evaluate đạt
sàn trước.

**I5.** Mỗi property có một người ghi duy nhất, và `PropertyOverlapValidator` gate mọi output. Mỗi
property (X, Y, ScaleX, ScaleY, Rotation, Alpha, Colour, Blending, các flip) của một object có đúng
một nguồn ghi; validator làm compile fail khi phát hiện chồng lấn, chạy sau khi flatten loop.

**I6.** Sàn tuyệt đối theo từng frame và từng tile. Vượt ngân sách thì đổi representation hoặc báo
violation, không bao giờ âm thầm hạ sàn.

**I7.** Thêm representation mới nghĩa là thêm `ICandidateProvider` (và nếu cần, một analyzer đề xuất
giả thuyết), không sửa lõi `ClosedLoopEncoder`, `ReconstructionState` hay verifier.

**I8.** Mọi số tạm phải được calibrate bằng đo trước khi trở thành gate.

**I9.** Chạy attribution sau mỗi milestone lớn; feature mới chỉ sinh ra từ dữ liệu bucket, không từ
tên feature nghe hay.

**I10.** `AssetStore` content-addressed là cơ chế dedupe duy nhất. Mở rộng (chuẩn hóa, biến thể
encoding) phải là mở rộng của hash key, không phải một cơ chế song song.

### 8.2 Bản đồ module

```text
src/OsbMpeg.Parsers/                        giữ nguyên toàn bộ (IR, OsbWriter, OsbvParser,
                                            CommandEvaluator, EasingTable, Passes)
                                            + M0: Ir/Passes/PropertyOverlapValidator.cs
                                            + M3: OsbWriter value-chaining shorthand

src/OsbMpeg.Compiler/
├── Compilation/
│   ├── VideoCompiler.cs                    sửa ở M1: điều phối pipeline mới
│   ├── VideoSourcePlanner.cs, VideoSourceKey.cs   giữ
│   └── GroupTransformBaker.cs              sửa ở M2: BakeTracked (fold camera, S2.5)
├── Detection/
│   └── ScenePrePass.cs, SceneBounds.cs     giữ (TileRunTracker vẫn phục vụ detection)
├── Analysis/                               mới ở M2
│   ├── FlowField.cs                        block matching pyramidal
│   ├── MotionModelFitter.cs                RANSAC similarity, sequential, interface layer-set
│   ├── MotionSegmenter.cs                  ranh giới thời gian khi model đổi chế độ
│   └── TrajectoryFitter.cs                 RDP joint, metric theo góc trong screen space
├── Encode/
│   ├── ClosedLoopEncoder.cs                mới ở M1: vòng chính mỗi scene hoặc segment
│   ├── ReconstructionState.cs              mới ở M1: display list cộng canvas incremental
│   ├── ErrorMap.cs                         mới ở M1: blockPSNR 16×16 cộng tileSSIM 64×64
│   ├── DirtyRects.cs                       mới ở M1
│   ├── FrameWindow.cs                      mới ở M1: cửa sổ trượt frame đã decode
│   ├── EmitContext.cs                      mới ở M1: sink, z-order, sổ sách lastEnd mỗi property
│   ├── Candidates/
│   │   ├── ICandidateProvider.cs           mới ở M1
│   │   ├── PatchProvider.cs                mới ở M1 (terminal, I3)
│   │   ├── CrossfadeProvider.cs            mới ở M1
│   │   ├── PhotometricProvider.cs          mới ở M1
│   │   ├── TransformTrackProvider.cs       mới ở M2
│   │   └── AnimationProvider.cs            mới ở M3 (hậu kỳ mỗi scene)
│   ├── AssetStore.cs                       sửa ở M3: tìm encoding, hash gồm cả encoding
│   ├── WorkloadBudgets.cs                  mới ở M3
│   └── EncodeOptions, Progress, Statistics giữ hoặc gọn lại
├── Evaluation/
│   ├── Metrics.cs                          sửa ở M0: thêm TileSsim
│   ├── WorkloadAnalyzer.cs                 mới ở M0
│   ├── QualityVerifier.cs                  mới ở M0
│   └── AttributionAnalyzer.cs              mới ở M3.5
└── Shared/                                 giữ: Media/ (FrameSource, MediaProbe, VideoFrame,
                                            FrameWriter), Render/ (Compositor sửa ở M0 để có
                                            bilinear, SoftwareStoryboardRenderer, CanvasVideoFrame),
                                            Analysis/ (TileGrid, TileRunTracker, ContentHasher)
```

Xóa ở M1 (chi tiết ở 9.4): `ParameterTuner`, `TileEncodeLoop`, `QuadtreeMerger`, `AnimationDetector`
(logic đọc lại từ git history khi làm `AnimationProvider`), `EncodePipeline`, `NaiveBaseline` (giữ
tới hết M0.5), `TileTimeline` nếu không còn consumer nào ngoài detection.

Tên file là đề xuất khớp cấu trúc repo hiện tại. Ranh giới module quan trọng hơn tên: đổi tên thì
được, đổi ranh giới thì phải hỏi Advisor.

### 8.3 Contract cốt lõi

Chữ ký được phép chỉnh cú pháp, không được đổi ngữ nghĩa.

```csharp
namespace OsbMpeg.Compiler.Encode;

/// Vùng dirty cần biểu diễn: bbox pixel-space đã coalesce, khoảng thời gian nó "nợ" nội dung.
public sealed record DirtyRegion(
    int X, int Y, int Width, int Height,      // pixel space, độ phân giải nguồn
    double ViolationStartMs,                   // frame vi phạm đầu tiên (backdating, S1.9)
    double NowMs);                             // frame hiện tại của vòng lặp

/// Ngữ cảnh encoder đưa cho provider; provider chỉ đọc.
public sealed class EncodeContext
{
    public required FrameWindow Window { get; init; }                 // frame đã decode, truy cập theo tMs
    public required ReconstructionState Reconstruction { get; init; } // trạng thái hiển thị hiện tại
    public required QualityPreset Preset { get; init; }
    public required WorkloadBudgets Budgets { get; init; }            // M3; trước đó là instance no-op
    public required SceneMotionInfo Motion { get; init; }             // M2; K=0 trước đó. Đây là gợi ý
                                                                      // phân tích: provider dùng để quyết
                                                                      // có propose hay không, không thay
                                                                      // pixel check (I4)
    public required AssetStore Assets { get; init; }
}

/// Một cách biểu diễn cụ thể cho một DirtyRegion. Stateless sau khi tạo.
public interface IRepresentationCandidate
{
    CandidateKind Kind { get; }
    long EstimatedCostBytes { get; }   // để sắp thứ tự trong một bậc; ước lượng rẻ, không cần đúng tuyệt đối

    /// Render thử composite của candidate lên bbox tại thời điểm tMs vào buffer scratch.
    /// Không side effect. Encoder gọi cho mọi frame candidate ảnh hưởng (I4).
    void RenderPreview(Span<byte> rgbScratch, int stride, double tMs);

    /// Ghi sprite và command vào EmitContext, trả về mô tả để encoder apply vào ReconstructionState.
    /// Chỉ được gọi sau khi đã evaluate. Trả về bytes thật đã ghi (asset mới đi qua AssetStore).
    CommitResult Commit(EmitContext emit);
}

public interface ICandidateProvider
{
    /// Bậc thang của provider; encoder thử theo thứ tự tăng dần. Patch = int.MaxValue-1 (terminal).
    int LadderRank { get; }
    /// Từ 0 tới n candidate cho region này. Không side effect. Rỗng nghĩa là "không áp dụng được".
    IEnumerable<IRepresentationCandidate> Propose(DirtyRegion region, EncodeContext ctx);
}
```

```csharp
/// Trạng thái hiển thị mô phỏng: working truth của encoder, không phải thẩm quyền (I2).
public sealed class ReconstructionState
{
    // Display list: mọi object đã emit cho scene này, theo thứ tự khai báo (tức z-order trong layer).
    // Chỉ mục không gian: lưới bucket 128×128 px để truy vấn object giao một bbox.
    public void AdvanceTo(double tMs);         // re-composite các bbox có primitive động
    public void Apply(CommitResult committed); // thêm object vào display list, composite ngay vùng nó phủ
    public ReadOnlySpan<byte> Canvas { get; }  // RGB24 độ phân giải nguồn
    public IReadOnlyList<ActiveObject> QueryRegion(int x, int y, int w, int h);
}

/// EmitContext là nơi duy nhất được ghi vào SbDocument cho một encode target.
public sealed class EmitContext
{
    // Bảo đảm I5 ngay tại nguồn: theo dõi lastEndMs theo cặp (object, property); API thêm command
    // từ chối chồng lấn ngay lúc emit. Validator cuối là lưới thứ hai, không phải lưới duy nhất.
    public SbSprite NewSprite(...);            // thứ tự khai báo bằng thứ tự gọi (quy ước z-order)
    public void AddCommand(SbObject obj, SbCommand cmd);
}
```

Vòng chọn candidate trong `ClosedLoopEncoder`, nơi I3, I4 và I7 sống:

```text
foreach region in dirtyRegions (sắp theo diện tích giảm dần):
    foreach provider in providers (sắp theo LadderRank):
        foreach candidate in provider.Propose(region, ctx) (sắp theo EstimatedCostBytes):
            ok = mọi frame candidate ảnh hưởng: metric(RenderPreview, source) đạt sàn
            if ok và Budgets.Allow(candidate):      # Budgets no-op trước M3
                r = candidate.Commit(emit)
                Reconstruction.Apply(r)
                goto region tiếp theo
    # không bao giờ tới đây: PatchProvider (rank cao nhất) luôn propose một candidate luôn pass
```

Rank hiện tại: `PhotometricProvider(10)`, `TransformTrackProvider(20, M2)`, `CrossfadeProvider(30)`,
`PatchProvider(int.MaxValue-1)`. `AnimationProvider` không nằm trong vòng này vì nó là pass hậu kỳ mỗi
scene. Provider được tự quyết không propose dựa trên gợi ý, ví dụ Crossfade thấy block đổi liên tục
thì trả rỗng ngay để khỏi tốn evaluate. Đó là cách giữ cho thứ tự thang không bị khóa cứng mà vẫn
đúng: bỏ propose chỉ có thể làm chậm hoặc tốn hơn (rơi xuống patch), không thể làm sai.

### 8.4 Luồng dữ liệu một lần compile

```text
.osbv parse ──► Sprite/Animation native đi thẳng ────────────────────────────► SbDocument
            └─► các object AnimationVideo
                  └─► VideoSourcePlanner (probe, gom theo file và fps)          [giữ]
                        └─► mỗi plan: ScenePrePass giới hạn theo window → ScenePlan[]  [giữ]
                              └─► mỗi member × mỗi scene giao nhau:             [sửa ở M1]
                                    FrameSource decode (độ phân giải nguồn, fps nguồn)
                                      └─► IFrameConsumer (N=1 ở bản này)
                                            └─► [M2] Analysis pass 1 (flow trên bản downscale
                                                 → motion model mỗi segment, ranh giới segment)
                                            └─► ClosedLoopEncoder mỗi segment:
                                                  frame → AdvanceTo → ErrorMap → DirtyRects
                                                  → ladder → Commit → Apply
                                                  (emit qua EmitContext, asset qua AssetStore)
SbDocument ──► pass IR: MergeAdjacentCommands → DropNoOpCommands → LoopExtractor
           ──► PropertyOverlapValidator (fail nghĩa là compile fail)            [M0]
           ──► OsbWriter → .osb                                                 [giữ]
           ──► QualityVerifier: parse .osb từ đĩa cộng asset từ đĩa → render mọi timestamp
               theo fps nguồn → report mỗi frame; vi phạm thì exit code khác 0, artifact vẫn ghi  [M0]
           ──► WorkloadAnalyzer report                                          [M0]
```

Về M2: phân tích cần flow giữa các frame liên tiếp. Quyết định là dùng hai lần decode cho scene có
motion (pass A chỉ downscale, nhanh, cho ra model và segment; pass B full-res để encode). Cách này đơn
giản, decode downscale rẻ, và tránh giữ cả scene trong RAM. Scene không có motion (spike hoặc gate từ
chối) chỉ cần một pass. Đừng xen kẽ analysis và encode trong cùng một stream ở bản này.

### 8.5 Ai sở hữu state nào

| State | Chủ sở hữu | Vòng đời | Ai khác được đọc |
| --- | --- | --- | --- |
| `SbDocument` | `VideoCompiler` | cả compile | các pass, writer, verifier (qua file) |
| Ghi command và sprite | `EmitContext` (duy nhất) | mỗi encode target | không ai |
| `ReconstructionState` | `ClosedLoopEncoder` | mỗi scene-segment và target | provider (đọc qua ctx) |
| `FrameWindow` | `ClosedLoopEncoder` | mỗi segment | provider (đọc) |
| `AssetStore` | `VideoCompiler`, một instance mỗi compile | cả compile | provider (GetOrAdd) |
| Motion model và segment | output của Analysis, record bất biến | mỗi scene | encoder, provider |
| Bộ đếm ngân sách | `WorkloadBudgets` | cả compile | encoder (Allow), report |
| Bản ghi chất lượng và vi phạm | `QualityVerifier` | giai đoạn verify | CLI report, exit code |

### 8.6 Kiểm tra ngược: kiến trúc có mở không

Câu hỏi kiểm tra: tuần sau phát hiện một representation mới tốt hơn mọi cái hiện có, có thêm được như
một candidate mà không phải viết lại `ClosedLoopEncoder`, `ReconstructionState` hay Verification
không? Câu trả lời là có, vì bốn lý do.

Lõi encoder chỉ biết `ICandidateProvider` và `IRepresentationCandidate`, nên thêm nghĩa là thêm class
rồi đăng ký vào danh sách provider kèm rank, không sửa vòng lặp. `ReconstructionState` composite từ
*object IR* trong display list chứ không từ "loại representation", nên nó render được candidate mới mà
không cần biết đó là gì; điều kiện duy nhất là primitive động mới phải nằm trong tập command mà
`CommandEvaluator` đánh giá được, và tập đó chính là toàn bộ command space của `.osb`. Verifier render
từ `.osb` nên theo định nghĩa không biết representation nào sinh ra file. Giả thuyết phân rã mới, ví
dụ depth layer, chỉ là một analyzer mới ghi thêm vào `SceneMotionInfo` cộng một provider mới tiêu thụ
nó.

Kẽ hở đã vá trước: nếu ladder được viết thành chuỗi if trong encoder thì I7 chết ngay. Vì vậy danh
sách provider kèm rank là bắt buộc từ M1, không phải một đợt refactor sau.

---

## 9. Lộ trình triển khai

Thứ tự: M0 → M0.5 → M1 → M2 → M3 → M3.5 checkpoint. Mỗi milestone một nhánh, kèm bảng A/B ghi vào
mục 13 của tài liệu này. Không milestone nào bắt đầu khi acceptance của milestone trước chưa xanh.
Fixture synthetic tạo bằng script ffmpeg trong `tests/fixtures/`.

### 9.1 M0: bộ máy kiểm chứng

Mục đích: mọi milestone sau đều cần một cái máy đo đáng tin trước khi tin bất kỳ con số nào. Không phụ
thuộc gì. Không đụng đường encode hiện tại, để bản cũ vẫn compile được làm baseline A/B.

**S0.1 Compositor bilinear (MUST).** Thêm sampling bilinear làm mặc định mới, giữ nearest qua tham số
enum cho test cũ. Sample tại tâm texel, clamp mép, không wrap. Alpha của asset tham gia lerp thẳng
(straight alpha, khớp blend đã verify). Thuật toán chuẩn: 4 texel lân cận, trọng số phân số, áp trước
tint và blend hiện có. Test: transform identity phải copy nguyên pixel; scale 2× của ảnh 2×2 cho từng
pixel kết quả tính tay trong test; rotation 90° là transpose; property test rằng output không vượt
min/max của các texel nguồn. Acceptance: test xanh, và render fixture golden không còn răng cưa khi
scale khác 1.

**S0.2 Metrics.TileSsim (MUST).** SSIM theo tile 64×64 luma (`luma = (77R+150G+29B)>>8`), lưới không
chồng, tile mép co lại theo phần còn lại và gộp vào tile kề nếu nhỏ hơn 16px. Trả về `(Min, P1, Mean)`
kèm chỉ số tile của Min. Công thức SSIM chuẩn với `C1=(0.01·255)²`, `C2=(0.03·255)²`, tính mean, var,
cov trực tiếp trên tile vì tile chính là cửa sổ. Không thay `Metrics.Ssim` toàn cục (giữ cho report),
không thêm perceptual metric. Test: `TileSsim(x,x).Min == 1`; phá một tile bằng cách invert thì Min
tụt mạnh còn Mean đổi ít, và test khẳng định bằng số cụ thể vì đây chính là chỗ SSIM toàn cục mù; ảnh
dịch 1px thì Min giảm rõ.

**S0.3 WorkloadAnalyzer (MUST).** Input là `SbDocument` cộng tra cứu kích thước asset. Output gồm
`PeakSteadySbLoad`, `PeakTransientSbLoad` (phần từ sprite đang trong cửa sổ fade), `PeakAliveSprites`,
`AnimationResidentBytes` (tổng `frames×w×h×4`, lấy max mỗi scene), `TotalAssetBytes`, `OsbLineCount`,
và một timeline mẫu mỗi 100ms. Thuật toán quét sự kiện: mỗi object có span sống là
`[minCmdStart, maxCmdEnd]` (bảo thủ, xem OQ-5); diện tích nhìn thấy tại t là bbox sau transform, đánh
giá qua `CommandEvaluator` tại các mốc 100ms; alpha=0 vẫn tính vào AliveSprites nhưng không tính vào
SbLoad, khớp R3. SB load bằng tổng diện tích chia cho 854×480. Acceptance: chạy trên output hiện tại
của minecraft (79 nghìn sprite) dưới 30 giây và in được số liệu.

**S0.4 PropertyOverlapValidator (MUST).** Chạy trên bản copy đã `LoopFlattener.Flatten`, không sửa
document thật. Với mỗi object và mỗi property (ánh xạ `SbCommandKind` sang property theo R5: M sang
{X,Y}, MX sang X, S sang {ScaleX,ScaleY}, V sang {ScaleX,ScaleY}), sắp theo StartMs rồi báo lỗi nếu
`next.StartMs < prev.EndMs - 0.5ms`. Command zero-duration cùng mốc: cho phép đúng một. Fail thì throw
`OsbValidationException` kèm chỉ số object, tên property và hai khoảng chồng nhau, và compile fail;
không tự sửa. Test: M chồng MX; S chồng V; chồng nhau sau khi flatten loop; và case hợp lệ nối đuôi
(`end == start`) phải pass.

**S0.5 QualityVerifier, subcommand verify, golden fixture (MUST).** Verification là giai đoạn bắt buộc
trong `CompileAsync`, chạy sau `OsbWriter`. Subcommand `verify <input.osbv> <output.osb> <assets>`
(ẩn) chạy lại giai đoạn đó độc lập.

Thuật toán: parse `.osb` từ đĩa qua `OsbReader`, đọc asset từ đĩa; với mỗi `AnimationVideo` trong
`.osbv`, xác định tập object do compiler sinh cho video đó. Verify chạy trong compile dùng tag
in-memory nên chính xác tuyệt đối, còn subcommand độc lập dùng heuristic theo tiền tố đường dẫn asset
của store (object passthrough native trỏ ra ngoài store nên bị loại). Giới hạn đã biết của chế độ độc
lập: hai `AnimationVideo` cùng layer chồng thời gian thì không tách được, khi đó in cảnh báo và verify
phần hợp; chấp nhận được vì chế độ độc lập chỉ để debug, thẩm quyền là giai đoạn trong compile. Render
composite các object thuộc video tại mọi timestamp theo fps nguồn trong window của video, so với frame
decode tương ứng, tính PSNR và TileSsim mỗi frame. Vi phạm thì ghi bản ghi (tMs, metric, tile). Kết
thúc thì in report mỗi scene (min, p1, mean) kèm danh sách vi phạm; artifact luôn được giữ; exit code
khác 0 nếu có vi phạm.

Golden fixture trong `tests/fixtures/golden/`: (a) một `.osb` viết tay có đủ M, MX, S, V, R, F, C, P
và easing 0, 2, 3 trên vài sprite, render tại 5 mốc so với PNG golden đã commit; (b) positive control
gồm một sprite full-frame crop chính xác từng pixel từ một frame video test.

Acceptance ba phần. Negative control: `verify` trên output hiện tại của bad_apple và fish phải chỉ ra
vi phạm, chứng minh bộ máy bắt được lỗi đang tồn tại. Positive control: fixture (b) phải pass 100% ở
`high`, để loại khả năng bộ máy hỏng theo kiểu fail mọi thứ. Golden (a) khớp pixel, cho phép sai ±1
trên 255 mỗi kênh do làm tròn.

Thứ tự M0: S0.1, S0.2, S0.4 độc lập nhau nên chạy song song được, rồi S0.3, rồi S0.5.

### 9.2 M0.5: spike calibrate preset

Mục đích: gỡ vòng lặp "M1 gate trên con số mà chính M1 mới calibrate". MUST. Phụ thuộc M0.

Subcommand ẩn `calibrate <video> [--window]` mô phỏng encoder thô, tức `ErrorMap` cộng hard-swap mọi
block vi phạm, chính là cận trên chi phí của một mức sàn. Quét sàn PSNR trong {30, 32, 34, 36, 38, 40}
nhân tileSSIM trong {0,80, 0,85, 0,88, 0,92}, đo patch count, asset bytes, phần trăm diện tích refresh
mỗi frame, và dump 3 frame PNG reconstruction mỗi combo để xem bằng mắt. `ErrorMap` được viết ở bước
này và M1 dùng lại.

Deliverable: bảng sàn so với chi phí mỗi fixture ghi vào mục 13, sáu con số preset commit vào
`QualityPreset` và cập nhật mục 2.3. Người dùng xem ảnh trước khi chốt; đây là điểm dừng chờ người
duy nhất giữa M0 và M1. Nếu dữ liệu cho thấy PSNR tối thiểu 34dB bất khả thi trên bad_apple hay
birdbrain thì preset chuyển sang lấy tileSSIM làm gate chính với PSNR thấp hơn.

### 9.3 M1: lõi closed loop

Mục đích: hết mờ, vì sàn tuyệt đối được bảo đảm bởi cấu trúc. Phụ thuộc M0 và M0.5. Không làm ở
milestone này: motion (M2), thực thi ngân sách (M3, `WorkloadBudgets` tồn tại nhưng `Allow()` luôn trả
true), animation (M3), JPEG (M3), shared decode nhiều target.

**S1.1 QualityPreset và CLI (MUST).** Record `(PsnrFloorDb, TileSsimFloor, HeadroomDb=2.0,
HysteresisFrames=2)`, flag `--quality high|medium|low` trên `compile`, mặc định high, số lấy từ M0.5.

**S1.2 ReconstructionState (MUST).** Display list mỗi (scene-segment × target), canvas RGB24 độ phân
giải nguồn. `AdvanceTo(t)` giữ danh sách object động (có command đang nội suy tại t: fade đang chạy,
sau này là motion track); hợp bbox hiện tại và bbox trước của chúng là vùng cần composite lại. Với mỗi
vùng: clear về đen (nền osu! là đen), rồi vẽ lại mọi object giao vùng theo thứ tự khai báo (truy vấn
lưới bucket 128px), mỗi object đánh giá state tại t qua `CommandEvaluator` rồi `Compositor.Blit`
bilinear. Object tĩnh ngoài các vùng đó giữ nguyên pixel. `Apply(commit)` thêm object vào display list
và bucket, rồi composite ngay bbox của nó, đúng vì nó là khai báo cuối nên nằm trên cùng.

Invariant nội bộ: canvas sau bất kỳ chuỗi `AdvanceTo` và `Apply` nào phải bằng kết quả render lại toàn
bộ display list tại t. Test đối chiếu này là test quan trọng nhất của M1: sinh document nhỏ ngẫu
nhiên, sau mỗi 10 thao tác ngẫu nhiên thì so canvas với bản render đầy đủ, phải bằng nhau từng byte.

**S1.3 ErrorMap (MUST, đã viết ở M0.5).** Mỗi block 16×16 tính SSD luma ra blockPSNR; mỗi tile 64×64
tính TileSsim. Output là bitmap block với ba trạng thái: sạch, cận sàn (dưới sàn cộng headroom), vi
phạm (dưới sàn). Viết bản scalar trước rồi đo; dùng `Vector<T>` nếu profile đòi.

**S1.4 DirtyRects (MUST).** Từ bitmap block (chỉ vi phạm và cận sàn đã chín): connected component
4-connectivity, lấy bbox mỗi component; bbox có tỷ lệ lấp dưới 0,5 và diện tích trên 64 block thì
split một lần theo hàng hoặc cột trống lớn nhất; cap mỗi rect 1024px mỗi chiều, vượt thì cắt; gộp các
rect nhỏ (dưới 32×32px) vào rect kề nếu cách dưới 32px, vì overhead PNG khoảng 1,5KB mỗi asset. Test:
các pattern bitmap cố định cho ra rect mong đợi, gồm hình chữ L, hai đảo tách rời, và full frame.

**S1.5 Khung candidate (MUST).** `ICandidateProvider`, `IRepresentationCandidate`, `EmitContext`,
`CommitResult` như 8.3. `EmitContext` thực thi lastEnd theo property (I5 ngay tại nguồn). Danh sách
provider đăng ký trong constructor của `ClosedLoopEncoder`, sắp theo `LadderRank`, tuyệt đối không
viết thành chuỗi if (I7).

**S1.6 PatchProvider (MUST, terminal theo I3).** Propose đúng một candidate: crop `region` từ
`Window.FrameAt(NowMs)`, `SbSprite` origin TopLeft, một lệnh `S` để đặt scale qua `CanvasMapping`, span
`[ViolationStartMs, openEnd]`. Run để mở: sprite sống tới khi bị patch sau thay thế, và `EmitContext`
vá EndMs của patch trước khi patch mới cùng vùng commit (encoder giữ sổ sách "patch hiện tại" mỗi
vùng). `RenderPreview` chính là crop đó, nên theo định nghĩa đạt sàn tại NowMs; các frame sau là "nội
dung mới nhất đã biết", hợp lệ vì frame sau lệch thì tự dirty. Frame 0 mỗi segment: encoder ép
PatchProvider full-frame, tức I-frame.

**S1.7 PhotometricProvider (MUST).** Chỉ propose khi có sprite trên cùng phủ ít nhất 90% region (truy
vấn `QueryRegion`) và region phủ ít nhất 90% bbox của sprite đó, vì `C` và `F` áp cho cả sprite chứ
không nửa sprite. Fit theo từng kênh bằng bình phương tối thiểu nhân: `c = 255·Σ(A·T)/Σ(A²)` với A là
nội dung đang hiển thị và T là target, clamp về [0,255]; nếu ba kênh xấp xỉ nhau và nhỏ hơn 255 thì
thử thêm biến thể chỉ dùng `F` khi phần dưới sprite là đen, tương đương fade to black. Emit `C` hoặc
`F` với span `[ViolationStartMs, NowMs]`, command sau nối tiếp nếu vẫn đổi. Không áp dụng khi có nội
dung khác nằm dưới sprite, vì `F` sẽ làm lộ thứ bên dưới; kiểm bằng `QueryRegion`. Fit hoặc verify
fail thì trả rỗng và ladder rơi xuống. Test: fixture fade to black phải chọn photometric (số asset mới
bằng 0 trong đoạn fade); tint đỏ dần cho ra track C.

**S1.8 CrossfadeProvider (MUST).** Chỉ propose khi vùng đã có patch hiện tại (không phải frame đầu) và
block không thuộc loại đổi liên tục: nếu từ `HysteresisFrames+2` frame gần nhất trở lên đều vi phạm
thì đó là thrash, trả rỗng, vì crossfade sai công cụ.

```text
B = crop source tại NowMs; A = nội dung đang hiển thị ở vùng này
thử span từ dài tới ngắn: t_s ∈ {NowMs−w, NowMs−w/2, NowMs−w/4, ...}, tối thiểu 2 frame
    với mỗi t_s (không sớm hơn thời điểm A bắt đầu đúng):
        mọi frame f ∈ [t_s, NowMs]: err(lerp(A,B,(f−t_s)/(NowMs−t_s)), source_f) có đạt sàn không
    pass đầu tiên thì emit: sprite B khai báo sau A, lệnh F,0,t_s,NowMs,0,1; A giữ sống tới NowMs
                 (EmitContext vá EndMs của A bằng NowMs); sau NowMs, B là patch hiện tại
không span nào pass thì trả rỗng, ladder rơi xuống PatchProvider
```

Kiểm tra lại quá khứ là hợp lệ: các frame trong `(t_s, NowMs)` trước đó đã pass với A tĩnh, còn
composite mới chỉ được nhận khi cũng pass, và thường pass đẹp hơn vì error giảm dần về B. Span
crossfade bị cap bằng kích thước cửa sổ trượt để mọi frame trung gian còn trong memory lúc quyết định;
quyết định định sàn không bao giờ dựa trên mẫu. Overdraw tính vào ngân sách transient riêng, không
tính vào cap steady, vì một dissolve toàn cục đưa cả frame vào hai lớp cùng lúc là hành vi đúng chứ
không phải thứ cần bị ngân sách ép về hard swap. Test: fixture `dissolve_synthetic` cho ra một cặp
asset thay vì chuỗi patch; nội dung đổi đột ngột giữa fade thì phải hard swap.

**S1.9 ClosedLoopEncoder (MUST).** Vòng lặp như 8.3, cộng hysteresis bất đối xứng và I-frame:

```text
frame 0 mỗi scene: full-frame refresh bắt buộc, vì R trống nên không có gì để chờ
với mỗi frame nguồn F_t (fps nguồn, không duplication):
    R.AdvanceTo(t)
    E = ErrorMap(R, F_t)
    dirty = block dưới sàn thật (preset.P): hành động ngay frame này, không debounce
    early = block dưới sàn cộng 2dB headroom: đợi 2 frame chống flap rồi mới thành dirty
    # hysteresis chỉ áp cho ngưỡng headroom, không bao giờ cho sàn thật, vì sàn tuyệt đối nghĩa là
    # không frame nào được ship dưới nó, kể cả trong cửa sổ debounce.
    # Patch emit sau debounce thì StartMs lùi về frame vi phạm đầu tiên (frame còn trong cửa sổ).
    if dirty rỗng: continue
    rects = CoalesceDirtyRects(dirty)
    mỗi rect: chạy ladder provider, commit cái đầu tiên đạt sàn
    R.Apply(emitted)
    assert (debug): ErrorMap lại vùng vừa sửa đạt sàn
```

**S1.10 FrameWindow (MUST).** Ring buffer `byte[]`, cap 2 giây theo fps nguồn (đo RAM lúc acceptance),
API `FrameAt(tMs)` và `Range(t0,t1)`. Frame ngoài cửa sổ không truy cập được, và đó chính là cách span
crossfade bị cap một cách tự nhiên.

**S1.11 Wiring, xóa code cũ, migration (MUST).** Thay lời gọi `TileEncodeLoop.RunAsync` bằng, cho mỗi
scene giao nhau và mỗi member, decode rồi `ClosedLoopEncoder.EncodeSegmentAsync(frames, target,
preset)`. Xóa `TunedFor`, `ParameterTuner`, `LoopOptions`. Baker giữ nguyên hành vi ở M1: sprite patch
và crossfade vẫn bake group transform qua `Baker.Bake` như cũ, phần fold theo track là việc của M2.

`ClosedLoopEncoder` nhận `IAsyncEnumerable<VideoFrame>` nên không biết gì về ffmpeg. Đây là ranh giới
decode và consumer: N=1 là ràng buộc của bản này chứ không phải nguyên tắc kiến trúc. Closed loop có
state theo từng target (mỗi target một `ReconstructionState` và một cửa sổ trượt, vì mapping và baker
khác nhau nên error map khác nhau), nên plan nhiều member chạy decode tuần tự theo từng member. Đây là
regression có chủ đích so với hiện tại, phải ghi vào CLAUDE.md. Nâng lên N lớn hơn 1 chỉ là nối lại
dây cộng tính toán memory N nhân cửa sổ, không đổi kiến trúc.

Xóa: `ParameterTuner` và test của nó, `TileEncodeLoop`, `QuadtreeMerger` và test, `AnimationDetector`
và test, `EncodePipeline`, `NaiveBaseline` (sau khi M0.5 xong), CLI `tune-bench`. Các subcommand ẩn
`bench`, `decode`, `probe`, `inspect` phụ thuộc `EncodePipeline`; đề xuất xóa `bench` và `probe`, giữ
`inspect` và `decode` nếu không phụ thuộc. Cần người dùng xác nhận trước khi xóa (OQ-6).

Giữ nguyên để tương thích: grammar `.osbv` và passthrough native; chữ ký CLI `compile` (thêm
`--quality`); bố cục asset `s/{hash}.png` và `a/{hash}/f{n}.png`; ngữ nghĩa cache asset xuyên lần chạy.

Acceptance M1: (1) 100% frame đạt sàn `medium` (số từ M0.5) trên cả 5 fixture, qua giai đoạn verify
độc lập chứ không qua `ReconstructionState`; (2) `dissolve_synthetic` dùng không quá 1/5 số asset so
với chế độ chỉ hard swap (chạy với CrossfadeProvider tắt để có baseline); (3) wall time trên
fish_spinning không tệ hơn hiện tại (tuning cộng encode), ghi số thật vào mục 13, mục tiêu tạm là
không quá 4 lần realtime 1080p30 *đã gồm verify pass*, trượt thì spike OQ-1 trước khi tối ưu mù; (4)
ghi nhận RAM đỉnh, chấp nhận tới khoảng 2,5GB ở 1080p, vượt thì giảm cap cửa sổ; (5) bảng A/B đầy đủ
so với bản hiện tại.

Cảnh báo hiệu năng từ chính lịch sử đo của repo: render cộng PSNR từng là chi phí trội và khó giảm
(mục 13.5), mà thiết kế mới render mọi frame full-res hai lần (một trong loop, một trong verify). Chi
phí verify scale theo overdraw và số sprite sống chứ không theo số frame, nên tệ nhất đúng trên các
fixture có patch rate cao.

Fallback tổng: encoder không bao giờ tạo ra output dưới sàn (I3); provider lỗi runtime thì log và coi
như trả rỗng để ladder rơi tiếp, nhưng không nuốt exception của PatchProvider vì provider terminal
fail nghĩa là bug thật và phải lộ ra.

Thứ tự M1: S1.1 → (S1.2, S1.3, S1.4 song song) → S1.5 → S1.6 → S1.9 (đã chạy được với patch) → S1.10
→ S1.7 và S1.8 → S1.11 → acceptance.

### 9.4 M2: motion (K=1 dưới interface layer-set)

Mục đích: hết nặng trên pan và zoom, vì nội dung do camera dịch chuyển ngừng bị re-emit mỗi frame.
Phụ thuộc M1. Không làm: K lớn hơn 1 (để dành checkpoint), panorama (để dành checkpoint), camera
rotation (bỏ ở bản này, thêm khi attribution đòi, `R` đã sẵn).

**S2.0 Spike ước lượng (MUST, trước khi xây phần còn lại).** Block matching chỉ translation trên
`pan_synthetic` và fish_spinning 5 giây, đo inlier ratio và residual. Quyết: block matching đủ, hay
cần pyramidal, hay cần DIS flow, theo đúng thứ tự đó (learned flow đã REJECT, xem 5.6). Phạm vi an
toàn: code spike ném đi được, chỉ số liệu ghi vào mục 13 mới là deliverable.

**S2.1 FlowField (MUST, thuật toán theo kết quả S2.0).** Phương án mặc định: downscale luma xuống tối
đa 256px (decode pass A riêng), mỗi block 16×16 tìm SAD trong bán kính ±12px, 3 mức pyramid nếu S2.0
đòi. Output là vector thưa kèm confidence theo SAD.

**S2.2 MotionModelFitter (MUST).** RANSAC similarity không rotation: model `(tx,ty,s)`, 2 điểm mỗi
mẫu, inlier là residual dưới 1px ở full-res, 64 vòng lặp. Interface trả
`IReadOnlyList<MotionLayer>`, bản này luôn 0 hoặc 1 phần tử, nhưng là layer-set ngay từ ngày đầu.
Gate hai tầng, cả hai phải đạt: inlier ít nhất 60% trong ít nhất 70% số frame; và diện tích residual
dự kiến (phần camera model không giải thích được) không quá 25% diện tích frame, tính trung bình trên
scene. Gate thứ hai tồn tại vì riêng inlier ratio cho phép accept một layer mà 40% frame vẫn phải
patch mỗi frame *chồng lên* một base sprite full-res cũng đang trả tiền; gate này đo thẳng cái quyết
định net win. Không đạt gate nào thì trả list rỗng và scene chạy thuần patch cộng crossfade, an toàn
tuyệt đối vì motion layer chỉ là optimization.

**S2.3 MotionSegmenter (MUST).** Từ chuỗi model mỗi frame: đoạn có `|v| < ε` kéo dài ít nhất 500ms
thành segment tĩnh, chuyển tiếp có hysteresis 300ms. Output là scene chia thành các segment
`[(t0, t1, MotionLayer?)]`. Encoder chạy theo từng segment, và I-frame ở đầu segment rơi ra tự nhiên
từ S1.9, chính là keyframe tại điểm đổi chế độ. Không tách segment cho thay đổi motion nhẹ, vì
overhead keyframe tự trả giá trong cost và ladder tự quyết.

**S2.4 TrajectoryFitter (MUST).** RDP joint: điểm là `(tx,ty,s)` mỗi frame, hàm khoảng cách là *dịch
chuyển lớn nhất của 4 góc frame* dưới transform đã compose, so giữa đường nội suy và giá trị thật,
tính bằng osu!px, epsilon tạm 0,3. Không chạy RDP riêng từng thành phần với epsilon theo đơn vị vị
trí: sai số scale Δs tốn `Δs·|x−c|` px, lớn nhất ở góc ảnh và gần như vô hình với epsilon đo trên
(tx, ty). Simplify joint cũng giữ breakpoint của `MX`, `MY`, `S` đồng bộ, điều kiện để dùng được
value-chaining shorthand ở M3.

Ghi chú toán: với `p(u) = p₀ + uΔp` và `s(u) = s₀ + uΔs`, vị trí màn hình của một điểm ảnh cố định x
là `y(u) = p₀ + s₀(x−c) + u·[Δp + Δs(x−c)]`, tức tuyến tính theo u trên mỗi segment. Nên M và S nội
suy tuyến tính đồng thời là đúng, chỉ có phép đo sai số phải nằm trong screen space.

**S2.5 TransformTrackProvider và fold vào baker (MUST).** Không có baker: sprite base full-frame (crop
tại keyframe đầu segment), origin Centre, emit `MX`, `MY`, `S` keyframe linear từ trajectory, giải
`p(t)` và `s(t)` từ `screen = p(t) + s(t)·(x−c)`.

Có baker thì phải giữ single-writer (I5): `GroupTransformBaker.BakeTracked(cameraSamples, ...)` compose
bằng cách lấy mẫu. Với mỗi frame nguồn t: lấy group transform tại t (logic `SampleAt` sẵn có) rồi
compose với camera `(p(t), s(t))`, ra chuỗi state cuối (x, y, sx, sy, α), rồi chạy RDP joint screen
space *một lần* trên kết quả và emit. Lý do lấy mẫu thay vì đại số: tích của hai track tuyến tính là
bậc hai, nên đừng làm đại số piecewise.

Đây chính là chỗ dễ sai nhất của toàn bộ thiết kế. `SbCommandKind` chỉ có một slot cho mỗi
MoveX, MoveY, Scale, VectorScaleX, VectorScaleY trên mỗi object, mà `GroupTransformBaker.Bake` đã phát
`MoveX`, `MoveY`, `VectorScaleX`, `VectorScaleY` hằng số lên *mọi* tile khi `positionConstant` đúng,
tức trường hợp thường gặp. Nếu motion layer phát track riêng cạnh track của baker thì đó là vi phạm
R5 và không tầng nào bắt được nếu thiếu `PropertyOverlapValidator`: `MergeAdjacentCommands` chỉ kiểm
tính liền kề, `OsbValidator` chỉ đếm object, còn `CommandEvaluator` giải quyết chồng lấn theo kiểu
"cái cuối trong danh sách thắng", một cách thứ ba khác cả stable lẫn lazer, nghĩa là verify có thể
pass sạch trong khi client thật render khác đi.

Base keyframe refresh: khi `ErrorMap` cho thấy vùng base (không bị patch đè) suy thoái tới ngưỡng cận
sàn trên hơn 40% diện tích thì mở keyframe mới, chuyển bằng `CrossfadeProvider` trên vùng full-frame,
tức tái dùng cơ chế chứ không viết riêng.

Provider rank 20, chỉ propose khi `ctx.Motion` có layer phủ region và region chưa có base đúng. Mép lộ
ra khi pan là coverage gap có chủ đích, không thuộc layer nên đi theo ladder thường và thành patch,
đúng như mong đợi. Guard 4K: nguồn có chiều nào vượt 3840 thì `SceneMotionInfo` trả rỗng và ghi lý do
vào log, vì base sprite ở độ phân giải nguồn sẽ chạm `MaxTextureSize`, mà cap diện tích 17Mpx² không
bắt được vi phạm theo từng chiều.

**S2.6 Fixture (MUST).** `pan_synthetic` (ffmpeg zoompan trên ảnh tĩnh lớn) và
`pan_foreground_synthetic` (pan cộng một sprite chuyển động độc lập chiếm khoảng 20% frame, thiết kế
để vừa lọt gate 60/70), script tạo trong `tests/fixtures/make_fixtures.ps1`.

Acceptance M2: (1) `pan_synthetic` asset bytes không quá 1/5 so với M1; (2) minecraft 10 giây không
quá 60% bytes so với M1; (3) `pan_foreground_synthetic` cho thấy gate hai tầng từ chối hoặc nhận đúng
theo net win, kiểm cả hai nhánh bằng cách chỉnh kích thước foreground; (4) 100% frame vẫn đạt sàn qua
verify độc lập; (5) scene bị từ chối motion cho output trùng byte với M1.

Thứ tự M2: S2.0 → S2.1 → S2.2 → S2.4 → S2.5 (nhánh không baker) → S2.3 → S2.5 (fold baker) → S2.6 →
acceptance.

### 9.5 M3: ngân sách, encoding asset, thrash

Phụ thuộc M1 cho ngân sách và encoding, M2 không bắt buộc cho encoding.

**S3.1 Thực thi WorkloadBudgets (MUST).** Bộ đếm cập nhật tại `Commit` (timeline SB load steady và
transient, alive count, animation resident). `Allow(candidate)` kiểm dự phóng sau commit so với ngân
sách ở 2.2. Không đạt thì encoder thử candidate hoặc provider tiếp theo, tức đổi representation chứ
không hạ sàn (I6). PatchProvider bị ngân sách chặn về lý thuyết không xảy ra vì patch rời nhau về thời
gian nên không tăng alive dài hạn; nếu vẫn xảy ra thì vẫn commit và ghi violation, vì fidelity thắng.
Test: ngân sách giả nhỏ làm crossfade bị chặn và rơi về patch, bản ghi violation đúng.

**S3.2 AnimationProvider và ThrashPlanner (MUST).** Pass hậu kỳ mỗi scene, không nằm trong vòng lặp
mỗi frame. Quét lịch sử commit theo vùng: vùng có ít nhất 4 hard swap liên tiếp cách nhau đúng một
frame và tổng span tuân cap (tối đa khoảng 120 frame, diện tích không quá tile 256px, tổng resident
không quá 200MB mỗi scene) thì gom chuỗi patch thành một `SbAnimation` LoopOnce và xóa các sprite
patch tương ứng khỏi document. Thao tác trên IR trước các pass, nên `EmitContext` cần hỗ trợ thay thế.
Gate uniqueness như bản cũ (ít nhất 80% frame khác nội dung), logic đọc lại từ git history rồi viết
gọn lại. Test: bad_apple 5 giây có animation resident trong ngân sách và giữ sàn; vùng nhấp nháy A/B
chỉ có hai nội dung thì *không* được thành animation, vì dedupe sprite thắng.

**S3.3 Tìm encoding asset (MUST).** Spike đầu tiên (OQ-4): tự tay bỏ một `.jpg` vào storyboard test,
load trong osu! stable và lazer, xác nhận render. Fail thì bỏ JPEG và chỉ giữ dither palette.

`AssetStore.GetOrAdd(pixels, w, h, consumer, EncodingPolicy)` thử các ứng viên gồm PNG thô, PNG palette
256/128/64 kèm Floyd-Steinberg, và JPEG q95/q90/q85 chỉ khi vùng opaque; decode lại, tính `TileSsim` so
với gốc, yêu cầu đạt `preset.TileSsimFloor + 0.02`, rồi chọn bytes nhỏ nhất. Hash key phải gồm cả
encoding đã chọn (I10), vì nếu không thì hai policy khác nhau đụng cùng một file; phần mở rộng file
theo format. Quyết định được cache theo content-hash nên asset đã gặp không phải tìm lại. Test:
gradient thì palette bị loại bởi gate SSIM, ảnh chụp thì JPEG thắng, và dedupe vẫn đúng khi hai policy
cùng chạy trong một compile.

**S3.4 OsbWriter value-chaining (MUST).** Chuỗi command cùng property, cùng độ dài bước, nối đuôi
nhau, cùng easing thì gộp thành một dòng nhiều cặp giá trị (R9). Test round-trip qua `OsbReader` và
validator OsuParsers; verify chạy trên file đã shorthand, và I2 sẽ tự bắt sai lệch ngữ nghĩa nếu có.

Acceptance M3: bad_apple giữ sàn với animation trong ngân sách; đường JPEG giảm ít nhất 30% asset
bytes trên birdbrain hoặc fish mà không thủng gate; không fixture nào vượt ngân sách mặc định hoặc có
violation report rõ ràng; `.osb` của minecraft 10 giây giảm ít nhất 20% nhờ chaining.

### 9.6 M3.5: checkpoint attribution

Mục đích: quyết định ưu tiên nghiên cứu tiếp theo bằng số liệu (I9). Đây không phải milestone feature.

**S3.5.1 AttributionAnalyzer (MUST).** Chạy trên kết quả compile toàn corpus cộng dữ liệu trung gian
của encoder (flow, model, commit log, serialize ra `attribution.json` khi bật `--attribution`).

| Bucket | Cách đo |
| --- | --- |
| `local-coherent-motion` | bytes của patch trong vùng mà sequential RANSAC pass 2 (chạy offline trong analyzer trên flow đã lưu) tìm được model K=2 giải thích ít nhất 70% block vùng đó |
| `pan-reveal` | bytes của patch nằm trong dải mép ngược hướng camera translation, bề rộng dải bằng `\|v\|·Δt` |
| `perspective-residual` | bytes patch ở vùng inlier ở tâm nhưng residual tăng theo bán kính (fit tuyến tính residual theo khoảng cách tâm, độ dốc vượt ngưỡng) |
| `near-duplicate-assets` | bytes của asset có coarse hash (aHash 8×8) trùng asset khác nhưng exact hash khác |
| `deformation` | patch còn lại có SSIM giữa hai lần refresh liên tiếp cùng vùng ít nhất 0,7, tức đổi có cấu trúc |
| `noise` | phần còn lại (SSIM giữa hai lần refresh dưới 0,7), bucket không cứu được |

Output là bảng phần trăm bytes theo bucket, theo từng fixture và trên toàn corpus, ghi vào mục 13.

**S3.5.2 Quy trình quyết định (MUST, đây là quy trình chứ không phải code).** Bucket lớn nhất từ
khoảng 25 đến 30% trở lên thì mở chu trình research, prototype, benchmark cho hướng tương ứng trong
menu: LayeredMotion K lớn hơn 1, Panorama, CanonicalAssetReuse, PerspectiveStrips. Bucket `noise` trội
thì dừng, ship, và ghi giới hạn vào README. Không đặt tên M4 hay M5 trước; milestone sau M3.5 chỉ tồn
tại sau khi attribution và benchmark của prototype thắng.

Kết quả của các research track ở mục 10 cũng đổ vào checkpoint này, thêm nguồn dữ liệu
(`representation_confusion`, `missing_primitive_attribution`, oracle gap) bên cạnh sáu bucket trên.

---

## 10. Research track (chờ duyệt)

Đây không phải milestone và không được tự chèn vào lộ trình. Chỉ khi một item thắng keep-gate của nó
thì mới viết phần spec tương ứng.

| ID | Item | Chạy sau | Deliverable | Keep-gate |
| --- | --- | --- | --- | --- |
| R1 | Dataset generator và benchmark truy hồi (tier rẻ cộng 5 đến 10 case tier vàng) kèm metric tầng hai ở 6.5 | M1 | `tools/OsbMpeg.DatasetGen`, dataset v1, report đầu tiên | là instrument, giữ nếu chạy được, không cần gate |
| R2 | Fit easing trong TrajectoryFitter (6.3) | M2 | prototype cộng A/B số keyframe và bytes trên dataset và fixture | giảm ít nhất 30% command bytes trên nội dung được author, 0 verify regression |
| R3 | Instrument đo oracle gap (6.4) | M3 | chế độ đo exhaustive cộng bảng gap | gap từ 10% trở lên thì đề xuất nâng bộ chọn; dưới 10% thì đóng câu hỏi ladder |
| R4 | Synthesis nhận biết loop (autocorrelation ra `L`) | tùy attribution ở M3.5 | chưa xác định | bytes `.osb` là trục lợi duy nhất vì asset đã dedupe, nên chỉ đáng nếu R1 hoặc attribution thấy chu kỳ phổ biến |

---

## 11. Bảng quyết định

Mỗi dòng một hướng, kèm điều kiện đảo quyết định. Không hướng nào bị khóa chết bằng ý kiến.

| Hướng | Bằng chứng | Lợi ích kỳ vọng | Độ phức tạp | Hợp với storyboard | Quyết định | Điều gì đảo nó |
| --- | --- | --- | --- | --- | --- | --- |
| Closed loop và sàn tuyệt đối | vòng hở là nguyên nhân gốc của mờ (1.1) | hết mờ do cấu trúc | trung bình | hoàn hảo, ở mức IR | MUST (M1) | không |
| Crossfade (T3) | blend lerp đã verify khớp shader; dissolve rất phổ biến | giảm mạnh refresh rate vùng đổi chậm | thấp | native (`F`) | MUST (M1) | không |
| Photometric (T8) | fade và tint là command native, gần 0 bytes | fade gần như miễn phí | thấp | native (`F`, `C`) | MUST (M1) | không |
| Global motion K=1 (T4) | R1 nội suy liên tục; 79 nghìn sprite là bằng chứng thiếu nó | thắng lớn nhất trên pan và zoom | trung bình | native (`M`, `S`) | MUST (M2), spike flow trước | S2.0 cho thấy flow không dùng được trên corpus thì thu hẹp về nội dung synthetic và game |
| Sub-segment theo motion | scene tĩnh rồi pan rồi tĩnh là ca thường | mỗi segment một representation đúng | thấp | không đổi | MUST (M2) | không |
| Layered motion K > 1 (T5) | lý thuyết vững; chưa đo tỷ trọng trên corpus | lớn nếu bucket local motion lớn | trung bình tới cao (mép layer, alpha matte) | tốt (K sprite xếp z) | DEFER tới M3.5 | bucket từ 25 tới 30% bytes |
| Panorama (T6) | toán khả thi (5.5); strip giải được giới hạn texture | cận dưới bytes cho pan dài | trung bình | tốt | DEFER tới M3.5 | bucket `pan-reveal` từ 25 tới 30% |
| Tái dùng asset chuẩn hóa | hash đã có; chuẩn hóa là mở rộng key (I10) | lớn nếu bucket near-dup lớn | thấp tới trung bình | hoàn hảo | DEFER tới M3.5 | bucket `near-duplicate-assets` từ khoảng 20% |
| Perspective strips | similarity thiếu shear, sai hệ thống và đo được | vừa, tùy nội dung | trung bình | chấp nhận được | DEFER tới M3.5 | bucket `perspective-residual` từ 25% |
| Suy luận occlusion riêng | là hệ quả của z-order cộng closed loop (5.3) | 0, vì đã có sẵn | không | không | REJECT (như một feature) | tìm ra ca mà closed loop không tự xử lý được |
| Object detection và tracking ngữ nghĩa | lệch granularity (5.6); lợi ích identity đã có đường rẻ hơn (5.4) | thấp so với giá | cao, kèm dependency nặng | kém | REJECT | một corpus thật nơi 4 layer motion thua rõ so với track theo object, sau khi layered motion đã thử |
| Depth estimation model | z-order suy được từ dữ liệu occlusion (6.2) | thấp | cao | gián tiếp | REJECT | layered motion bế tắc riêng vì không suy được thứ tự z |
| Learned optical flow | classical khớp đúng granularity rect (5.6) | chỉ khi classical fail | cao (ONNX, model, GPU) | không đổi | REJECT ở giai đoạn này | S2.0 và cả DIS flow đều fail trên một bucket lớn |
| RDO Lagrangian, graph search toàn cục | fidelity là ràng buộc cứng chứ không phải λ; bài học predict so với measure (13.4) | thấp | cao | không đổi | REJECT | đo được greedy ladder bỏ phí hơn 10 tới 15% trên corpus |
| DP đặt keyframe | RDP đủ theo mọi số đo hiện có | nhỏ | thấp | không đổi | DEFER | đo thấy RDP bỏ phí keyframe đáng kể |
| Fuzzy hoặc embedding cho near-dup | O(N²) và rủi ro sàn; canonical exact phủ phần lớn (5.4) | nhỏ so với canonical | cao | không đổi | REJECT | canonical reuse đã ship mà bucket near-dup vẫn lớn |
| Animation fallback (T7) | thrash có thật (bad_apple); R4 giới hạn memory | chặn nổ sprite count | thấp | native, đắt memory | MUST (M3), có cap | không |
| JPEG asset | dữ liệu DEFLATE cũ cho thấy ảnh chụp nén kém bằng PNG | trên 30% bytes trên nội dung ảnh chụp | thấp | cần OQ-4 xác nhận | MUST (M3) sau spike | OQ-4 fail thì REJECT |
| WebP hoặc AVIF | chưa có bằng chứng osu! load được | chưa rõ | thấp | chưa xác nhận | REJECT cho tới khi verify | xác nhận cả stable và lazer load được |
| Fit easing và hàm trong TrajectoryFitter | lý thuyết vững, fit dạng đóng nên rẻ (6.3) | lớn trên nội dung được author, gần 0 trên trajectory nhiễu | thấp | native (easing 0 tới 34) | INVESTIGATE (R2, sau M2) | keep-gate ở mục 10 |
| Dataset ground truth và metric tầng hai | generator và renderer đã có; mở khóa câu hỏi "chọn sai ở đâu" | là hạ tầng đánh giá | trung bình | là instrument | INVESTIGATE (R1, sau M1) | không cần gate; sai lệch tier rẻ so với tier vàng tự nó là phát hiện |
| Bộ chọn best-among-passing | bậc chi phí cách nhau bậc độ lớn nên nghi ngờ gap nhỏ; chưa đo | nhỏ tới vừa | thấp | không đổi | DEFER tới R3 | gap từ 10% trên dataset và fixture |
| Synthesis nhận biết loop | `.osb` bytes là trục lợi duy nhất; LoopExtractor hậu kỳ đã có | nhỏ trừ khi nội dung có chu kỳ | trung bình | native (`L`) | DEFER (R4) | R1 hoặc attribution thấy chu kỳ phổ biến |
| Prune nhiều độ phân giải trong Evaluate | chỉ loại chứ không nhận nên an toàn với I4 | chỉ về hiệu năng | thấp | không đổi | DEFER | profile cho thấy evaluate là nút cổ chai |
| Score truy hồi tổng hợp | chưa có dữ liệu về tương quan giữa metric và "truy hồi đúng" | chưa rõ | chưa rõ | không đổi | REJECT cho tới khi có dữ liệu | benchmark R1 cho thấy các metric rời tương quan mạnh và cần một số duy nhất |
| Mesh hoặc subdivision tổng quát | nổ sprite count; thiếu shear làm lưới thô sai hệ thống | chưa rõ | cao | kém (fill-rate) | REJECT | chưa thấy đường đảo |
| `[Variables]` để nén `.osb` | R9, 3 giây thành 45 giây | âm | không đổi | không đổi | REJECT vĩnh viễn | không |

---

## 12. Câu hỏi mở

| ID | Câu hỏi | Vì sao chưa quyết | Spike quyết định | Phạm vi an toàn |
| --- | --- | --- | --- | --- |
| OQ-1 | Chiến lược re-composite nào của `ReconstructionState` nhanh nhất | vấn đề hiệu năng, không phải tính đúng | benchmark 2 chiến lược trên fish 5 giây | chỉ đổi bên trong class, giữ test đối chiếu render đầy đủ |
| OQ-2 | Block matching có đủ tốt trên footage thật không | chất lượng flow phụ thuộc nội dung | S2.0 | code spike ném đi được, chỉ số liệu là deliverable |
| OQ-3 | Con số preset cuối cùng | phụ thuộc nội dung | M0.5 | chờ người dùng xem ảnh, không tự chốt |
| OQ-4 | osu! stable và lazer có load asset `.jpg` trong storyboard không | chưa verify thực tế | đầu S3.3 | khoảng 30 phút thủ công; fail thì bỏ JPEG, không tìm cách lách |
| OQ-5 | Ngữ nghĩa alive chính xác cho `WorkloadAnalyzer` (alpha lớn hơn 0 lần đầu so với span command) | ảnh hưởng độ chính xác report, không ảnh hưởng output | hỏi cụ thể qua deepwiki khi cần | dùng bản bảo thủ (span command) tới lúc đó |
| OQ-6 | Số phận các subcommand ẩn `bench`, `decode`, `probe`, `inspect` | tùy người dùng còn dùng hay không | không có | không xóa khi chưa được xác nhận; compile lỗi thì hỏi |

---

## 13. Lịch sử đo đạc của bản cài đặt hiện tại

Phần này giữ lại các kết quả đo và các hướng đã thử từ tài liệu research cũ, để không ai phải suy diễn
lại từ đầu hay đề xuất lại thứ đã bị bác bỏ có bằng chứng.

### 13.1 Motion và region compensation (đã bác bỏ)

`GlobalMotionEstimator`, `RegionSegmenter`, `RegionTracker`, `TransformEstimator`, `TrajectoryFitter`,
`OcclusionAnalyzer` từng bị bác bỏ vì lý do cấu trúc chứ không phải chưa tinh chỉnh. `.osb` không có
predictive hay residual coding, mỗi Sprite là một texture đầy đủ và tự chứa. Một motion vector chỉ
dùng để *dự đoán* tile lưới cố định tại (x,y) từ frame trước không mua được gì: bytes của asset vẫn
đến từ việc crop vùng (x,y) cố định của frame *hiện tại*, mà vùng đó đổi mỗi frame dưới một cú pan bất
kể vector nói gì. Muốn asset trùng byte qua các frame khi có chuyển động thật thì phải dịch lưới *lấy
mẫu* theo nội dung, nhưng khi đó lưới *xuất* cũng phải dịch đúng như vậy để dựng lại cho đúng, và điều
đó mở ra khoảng trống ở một mép cùng phần thừa ở mép kia.

Điểm khác biệt của hướng mới ở mục 5: asset là sprite bền di chuyển bằng lệnh `M` và `S`, không
re-crop mỗi frame, nên motion vector trực tiếp thành command. Coverage gap ở mép pan là có chủ đích và
được closed loop xử lý như mọi vùng dirty khác. Tiền đề của lần bác bỏ cũ không còn.

Một nhánh chưa chết nhưng chưa từng được đo: residual coding có dấu bằng một Sprite thứ hai blend
normal lên trên một base, với dấu và độ lớn nướng vào RGBA từng pixel. Đã xác nhận biểu diễn được từ
fragment shader của osu-framework (`sh_Masking.h`: `return v_Colour * texel;`, alpha của texel thật sự
tham gia blend). Cái chặn nó: normal blend là `lerp(prediction, overlay, alpha)` theo từng kênh, nên
tái tạo chính xác một target bất kỳ cần một màu overlay theo pixel, mà điều đó tương đương suy lại
chính ảnh target, tức không dedupe được gì trừ khi màu overlay suy biến về một palette dấu nhỏ (ví dụ
đen và trắng), và khi đó một alpha phải dùng chung cho R, G, B nên chỉ đúng ở nơi cả ba kênh có delta
cùng dấu. Chưa bao giờ đo: trên footage thật, tỷ lệ pixel có `sign(ΔR)=sign(ΔG)=sign(ΔB)` là bao
nhiêu, và cặp (sign map, alpha map) có dedupe tốt hơn tile thô không. Ngoài ra base sprite không thể
đóng run khi còn residual phụ thuộc vào nó, tức gấp đôi số asset cho mỗi tile được phủ.

### 13.2 AssetTrimmer (đã bác bỏ)

Cơ chế nén thì hoạt động: một phép thử DEFLATE (ffmpeg, RGB và alpha bị zero dưới một mask tổng hợp,
khoảng 14 mẫu) cho thấy asset lớn hơn 3KB giảm được 25 tới 74% (trung bình khoảng 45%), còn asset nhỏ
hơn 1,5KB thì *tệ đi*, vì header và chunk của PNG chiếm ưu thế ở cỡ đó. Đây là điểm giao đo được,
không phải phỏng đoán.

Nhưng cả hai biến thể (mask alpha giữ nguyên footprint, và crop thật) đều dính một lỗi đúng đắn: các
sprite tại một vị trí tile có lifetime không chồng nhau theo thiết kế, run B bắt đầu đúng lúc run A
kết thúc. Nếu asset của run B làm trong suốt các pixel "không đổi so với run A" để tiết kiệm bytes thì
trong suốt lifetime của run B chúng render thành canvas trống, vì sprite của run A đã bị gỡ từ trước.
Muốn quay lại hướng này thì cần một mô hình lifetime hoàn toàn khác, kiểu sprite không bao giờ bị gỡ
và được vá dần.

Bài học đó đã được tôn trọng trong thiết kế crossfade ở 9.3: sprite cũ được giữ sống cho tới khi
sprite mới opaque hoàn toàn.

### 13.3 Tầng RDO (đã bác bỏ)

Trước khi xây bất kỳ framework nào, một phép đo được đặt làm điều kiện: quét
`--min-animation-uniqueness` (ngưỡng heuristic chọn Sprite so với Animation sẵn có) xem một lần tinh
chỉnh mặc định đơn giản có bắt được các ca mà framework theo chi phí lẽ ra cần cho không. Đã quét hai
lần, trước và sau một sửa lỗi không liên quan ở `QuadtreeMerger`, và output trùng byte cả hai lần trên
fixture test, trước cũng như sau. Cả hai lần thắng thật về nén trong phiên đó đều truy về việc sửa bug
cơ học ở *phía trên* tầng chọn candidate (một lỗi làm tròn và một lỗi tiêu chí merge), không phải do
bản thân heuristic chọn sai. Không xây; tiền đề (một ca mà ngưỡng uniqueness về cấu trúc không thể
biểu đạt lựa chọn đúng) vẫn chưa được chứng minh trên mọi fixture đã thử.

### 13.4 Phân hoạch tile theo heatmap (spike, không nhận)

Ý tưởng: thay vì lưới tile đều cố định, dựng heatmap tần suất thay đổi từ một lần quét mịn rồi phân
hoạch canvas từ trên xuống, vùng lớn ở nơi nội dung tĩnh và vùng nhỏ ở nơi nội dung thay đổi liên tục.

Lần thử đầu có một lỗi phương pháp thật, bị bắt trước khi tin vào số: quyết định split dùng tần suất
thay đổi *trung bình* của mọi tile mịn trong vùng ứng viên. Trung bình che đúng ca cần tránh: một góc
nhỏ biến động (ví dụ một vật thể di chuyển chiếm một phần vùng lớn) ép hash tổng hợp của *cả* vùng đổi
gần như mỗi frame bất kể phần còn lại tĩnh tới đâu, nhưng trung bình toàn vùng vẫn thấp nên không bao
giờ kích hoạt split. Kết quả: cả canvas 1920×1080 của fish_spinning giữ nguyên một vùng, re-encode PNG
full-frame khoảng 600 lần, 202MB cho cửa sổ 10 giây. Cùng nguyên nhân gốc với bug merge một frame của
`QuadtreeMerger`: một tín hiệu tổng hợp bỏ sót biến động cục bộ. Sửa bằng cách đổi tiêu chí split sang
tần suất thay đổi *lớn nhất* của bất kỳ tile mịn nào trong vùng.

Spike sau khi sửa, đo chính xác theo byte trên ba fixture, dùng `ContentHasher` thật và `AssetStore`
in-memory để có chi phí PNG thật, so với output có tuning theo scene của bản hiện tại:

| Fixture | Window | Adaptive assetBytes | Tuned assetBytes | Tỷ lệ |
| --- | --- | --- | --- | --- |
| fish_spinning | 10s | 201.209.266 | 76.154.338 | tệ hơn 2,6 lần |
| minecraft | 10s | 375.766.670 | 69.576.841 | tệ hơn 5,4 lần |
| short_animation | 16,5s | 3.714.496 | 1.442.462 | tệ hơn 2,6 lần |

Thua cả ba, kể cả fixture nội dung sạch mà ý tưởng lẽ ra được lợi nhất (short_animation: 2294 vùng
phát ra so với 116 sprite của hệ thống thật trên cùng nội dung).

Hai điểm gây nhiễu đã biết, tức đây không phải cuộc so hoàn toàn công bằng: spike hardcode Colors=0
(không lượng tử hóa palette PNG) trong khi combo đã tune của short_animation dùng Colors=16; và ngưỡng
split (tần suất thay đổi lớn nhất trên 0,15) chỉ được thử đúng một lần, không quét. Nhưng có tín hiệu
thật bên dưới: bộ tuner theo scene *tự chọn* TileSize=256, tức vùng lớn và đều, chấp nhận chi phí
re-emit, cho đúng các fixture chuyển động mạnh mà ý tưởng này nhắm tới, ngược hẳn với dự đoán của phân
hoạch mịn theo nội dung.

### 13.5 Những thứ đã sửa và thắng đo được

**Bug merge một frame của `QuadtreeMerger` (commit `cdb5820`).** Tiêu chí merge duy nhất của
`TryMergeBlock` là `run.StartMs == first.StartMs && run.EndMs == first.EndMs`, tức sub-tile merge với
nhau bất cứ khi nào chúng đóng cùng thời điểm, không kiểm tra nội dung tương tự chút nào. Ở vùng
chuyển động nhanh nơi mọi tile kề nhau đổi mỗi frame, cả bốn đóng cùng nhịp thuần túy vì cùng biến
động mạnh, thỏa mãn phép thử thời gian mà không chia sẻ chút nội dung nào. Tìm ra qua một ca cụ thể:
vị trí tile chiếm 399 trên 425 sprite của `fish_spin_test` hóa ra là một block 640×640 do
`QuadtreeMerger` gộp (xác nhận bằng `ffprobe` trên PNG thật, không phải tile gốc 320×320), 100% khác
nhau giữa các frame, và vĩnh viễn không đủ điều kiện để `AnimationDetector` nâng cấp. Sửa: từ chối
merge khi khoảng `[StartMs,EndMs]` chung chỉ dài một frame, vì merge dài một frame theo định nghĩa
không bao giờ biểu diễn được nội dung tĩnh dùng chung thật sự. Thắng đo được trên `fish_spin_test`:
sprite từ 720 xuống 344 (giảm 52%), command từ 834 xuống 445 (giảm 47%), `.osb` từ 79,06KB xuống
44,63KB (giảm 44%), PSNR và SSIM *tăng* (35,12 lên 37,81 dB; 0,9687 lên 0,9847).

Một bug nhỏ hơn sửa cùng lúc: phép kiểm `isSingleFrame` của `AnimationDetector` dùng dung sai 0,5ms so
với thời lượng frame thật (phân số). Ở 30fps (33,333ms mỗi frame), biên run bị lượng tử hóa về ms nên
xen kẽ 33ms và 34ms theo tỷ lệ khoảng 2:1, mà dung sai 0,5ms chỉ chấp nhận được một phía, phân loại
sai khoảng 29% run một frame thật thành "ổn định". Đã nới lên 1,0ms.

**`FrameSource` treo vĩnh viễn với input không decode được.** Reader của named pipe (`StreamPipeSink`)
chặn vô hạn nếu ffmpeg không bao giờ mở phía output, ví dụ nó thoát ngay vì file input không tồn tại
nên không bao giờ tới bước mở pipe. Đã xác minh tiến trình thoát sạch (khoảng 1,3 giây với một lần
chạy ffmpeg thật trên file không tồn tại): treo hoàn toàn nằm ở phía .NET, `NamedPipeServerStream` chờ
một kết nối không bao giờ tới, và việc tiến trình đã thoát không tự gỡ được cái chờ đó.

Ba lần sửa hụt, mỗi lần được đo là sai chứ không phải phỏng đoán: (1) hủy khi task của
`FFMpegArguments.ProcessAsynchronously()` hoàn thành, nhưng task đó không trả về cho tới khi I/O của
sink xong, tức nó đang kẹt đúng chỗ đó; (2) chỉ giới hạn `ReadAsync` đầu tiên trong callback của sink,
nhưng bắt tay kết nối xảy ra *trước* khi delegate của sink được gọi trong trường hợp client không bao
giờ kết nối; (3) `CancellableThrough(token)` để giết tiến trình ffmpeg, vô nghĩa khi tiến trình đã tự
thoát.

Cách thật sự hiệu quả: chỉ giới hạn phần chờ channel của *chính mình*
(`ChannelReader.WaitToReadAsync(token)`, hoàn toàn nằm trong tầm kiểm soát của phương thức đang gọi,
độc lập với nội bộ FFMpegCore) cho riêng item đầu tiên. Khi dữ liệu thật đã chảy một lần thì nhịp
decode trở lại bình thường và không giới hạn. `StartupTimeout` ban đầu là 20 giây, sau nới lên 60 giây
khi tuning theo scene chạy song song khiến vài lần decode ffmpeg có thể tranh CPU cùng lúc; input hỏng
thật vẫn fail trong khoảng 1,3 giây nên nới timeout không che lỗi thật.

**`VideoId` từng là bộ đếm theo document, không phải danh tính ổn định.**
`VideoSourcePlanner.PlanAsync` gán `VideoId = i.ToString("x")`, tức thứ tự gặp trong document của một
lần compile. Vì `VideoId` đặt tên thư mục con của cache asset, hai project `.osbv` tham chiếu cùng một
video nhưng khác số lượng hoặc thứ tự nguồn lại rơi vào *hai* thư mục asset khác nhau cho cùng một
file, không chia sẻ gì. Đã sửa: hash ổn định của `(NormalizedPath, EffectiveFps)` qua `XxHash128`. Xác
minh thật: compile một document `.osbv` hình dạng khác tham chiếu cùng video ở vị trí nguồn thứ hai
vào cùng thư mục asset, thì `scenes.json` và file asset của video dùng chung được đọc thẳng từ cache
(không có dòng log tune lại nào), và tên thư mục `VideoId` trùng byte với document ban đầu.

**`Colors` bị cố định lúc dựng `AssetStore` thay vì theo từng lần gọi.** Phát hiện giữa lúc làm tuning
theo scene, tức là một bug mà tính năng đó *sẽ tạo ra* nếu không bắt kịp: `AssetStore.GetOrAdd` và
`WriteAnimation` hash trên bytes RGB thô, còn `Colors` cố định lúc dựng. Ổn khi một combo phủ cả
video, hỏng ngay khi hai *scene* dùng chung một `AssetStore` mà chọn `Colors` khác nhau cho cùng pixel
thô, vì scene thứ hai sẽ âm thầm dùng lại thứ scene đầu đã ghi (chế độ hexNaming bỏ qua encode lại khi
đường dẫn đích đã tồn tại), sai cả mức lượng tử hóa. Đã sửa bằng cách gấp `Colors` vào content hash
làm seed của XXH3.

### 13.6 Những thứ đã ship và số liệu của chúng

**Cache asset content-addressed bền (XXH3-128, tên theo hex).** Thay SHA-256 cắt ngắn (64 bit) bằng
XXH3-128, tốt hơn hẳn ở cả hai trục chứ không phải đánh đổi. Đường dẫn ở chế độ `hexNaming` suy từ
content hash (`s/{hash:x32}.png`) nên `SavePng` bỏ qua encode lại khi file đích đã tồn tại, biến thư
mục asset thành cache thật giữa các lần chạy. Xác minh trực tiếp: compile cùng fixture hai lần, lần
lạnh ghi 1102 file trong 25,1 giây, lần ấm không chạm file nào trong 19,1 giây (mtime từ `stat` giống
hệt trước và sau), output `.osb` trùng byte. Trên fixture nhiều animation (tỷ lệ chi phí encode PNG so
với decode cao hơn), tăng tốc nhờ cache ấm hơn 2 lần.

Điều được xác lập ở đây và có ảnh hưởng tới mọi thứ phía sau: thay đổi `TileSize`, `HashQuantLevels`
hay `Colors` *không* trung tính với cache chỉ vì một giá trị khác thì sinh nội dung khác một cách hợp
lệ. Hash phủ cả buffer tile, nên đổi `TileSize` đổi luôn độ dài hash của *mọi* tile, không chỉ những
tile có nội dung nhìn thấy thay đổi. Một lần chạy ở `TileSize=160` không có lấy một cache hit nào so
với cache của lần chạy trước ở `TileSize=320`, ngay cả trên cùng footage. Điều này *đúng* (tile cỡ
khác thì đúng là asset khác) nhưng nghĩa là mọi bộ auto-tune phải hội tụ về một giá trị ổn định cho
mỗi nội dung và giữ nguyên qua các lần compile lại.

**Auto-tune toàn cục.** Luôn bật, không có flag CLI. Sàn chất lượng tự hiệu chuẩn: probe một lần ở
mặc định hardcode trên một cửa sổ mẫu ngắn, lấy PSNR đó *trừ* một khoảng nới nhỏ (1dB) làm sàn cho mọi
candidate. Tìm kiếm theo coordinate descent thay vì lưới 4 chiều (13 tới 15 probe thay vì 81 tới 625):
`TileSize` trước (đòn bẩy cấu trúc lớn nhất), rồi `Colors`, rồi `HashQuantLevels`, rồi `TileTolerance`
cuối cùng. Tín hiệu chi phí là asset bytes *cộng* ước lượng bytes text của `.osb`
(`commandCount * ~100`), không phải asset bytes đơn thuần.

Thắng đo được trên cả 5 fixture, một cửa sổ 10 giây mỗi fixture, combo đã tune so với mặc định
hardcode cũ:

| Fixture | Nội dung | Combo đã tune | Δ bytes | Δ sprites |
| --- | --- | --- | --- | --- |
| short_animation | animation sạch | 256/32/8/16 | -25,2% | 90 trên 911 |
| birdbrain | footage thật, nhiễu | 64/32/8/0 (bằng mặc định) | 0,0% | 8549 trên 8549 |
| bad_apple | nhấp nháy đen trắng cực đoan | 64/16/8/0 | -4,4% | 10007 trên 12398 |
| fish_spinning | FHD, nhiễu vừa | 256/32/8/0 | -7,7% | 875 trên 6705 |
| minecraft | game render, sạch | 256/16/8/0 | -28,9% | 842 trên 14535 |

Không fixture nào tệ đi. Footage thật nhiễu rơi đúng vào mặc định hardcode, tức sàn an toàn hoạt động
như thiết kế. Số sprite và command giảm mạnh hơn hẳn số bytes (Minecraft: bytes giảm 28,9% nhưng sprite
giảm 94%), nghĩa là engine storyboard của osu! xử lý ít object hơn nhiều so với những gì con số bytes
gợi ý. Đây cũng là bằng chứng sớm rằng mô hình chi phí ở 2.2 tách bytes khỏi workload là đúng.

Chi phí tuning đã được đo và cắt dần, mỗi bước đo trước khi sang bước sau: 5 phút 31 giây (lần chạy
thật đầu tiên, 14 probe, mẫu 4000ms), xuống 2 phút 8 giây (thu cửa sổ mẫu còn 1500ms, vì thời gian
render mới là chi phí trội chứ không phải số lần decode hay số lần spawn ffmpeg, tìm ra bằng đo từng
probe chứ không phải phỏng đoán), xuống 1 phút 25 giây (song song hóa các candidate trong cùng một
trục), xuống 30 giây (bỏ probe trùng khi mang seed sang trục sau).

**Tuning theo từng scene.** Động cơ đến từ hai thí nghiệm ném đi trước khi quyết xây: một fixture hai
scene có hard cut (giảm 8,9% bytes so với một combo toàn cục) và một fixture ghép 5 fixture (giảm
26,7%). Cảnh báo được ghi ngay lúc đó: 26,7% là trần, vì đó là 5 nguồn khác nhau tối đa bị cắt mỗi 5
giây, ca thuận lợi nhất mà tuning theo scene có thể gặp. Đo trên code đã ship thật (ép cùng một đường
code production đi qua nhánh "1 scene" so với "N scene" để so sánh tương đương): **giảm 7,86%** trên
một fixture 8 scene thật, khớp với cảnh báo "đừng kỳ vọng trần".

Phát hiện scene boundary (`ScenePrePass`, chỉ decode, không PNG, không QuadtreeMerger, không
AnimationDetector) tìm hard cut qua một tín hiệu duy nhất: một frame mà tỷ lệ rất cao các vị trí tile
đóng run đồng thời, dưới *bất kỳ* bộ tham số nào, phát hiện thẳng từ giá trị trả về của
`TileRunTracker.Advance`.

Một lỗi hiệu chuẩn thật, tìm ra qua chính phần verification của plan chứ không phải bỏ qua bằng giả
định: ngưỡng đoán ban đầu `CutThreshold=0.8` bỏ sót một cut thật đo được ở 0,721, trong khi một frame
chuyển động mạnh bình thường (không phải cut) đo được 0,662; đã hiệu chuẩn lại về 0,7, nằm đúng giữa
hai số đo thật. Quy tắc chặn merge ban đầu ("từ chối mọi ứng viên trong vòng 2000ms kể từ cut được
chấp nhận gần nhất") nuốt mất một cut thứ hai thật chỉ cách cut đầu 1,8 giây; đã thay bằng thuật toán
hai pass: gom cụm các frame vượt ngưỡng gần nhau thành một ứng viên trước (dung sai khoảng cách
300ms), rồi chỉ từ chối một ứng viên nếu nó tạo ra một scene dài gần bằng 0 ngay sau scene trước
(1000ms, chỉ để chặn ca suy biến).

**Tuning lười theo scene.** Bản ship ban đầu tune *mọi* scene phát hiện được trên cả file ngay sau
prepass. Một truy vấn `.osbv` cửa sổ 10 giây trên file 255 giây có nhiều cut tự nhiên phải trả khoảng
100 tới 170 giây tuning *mỗi scene*, cho những scene chẳng liên quan gì tới cửa sổ được yêu cầu. Sửa:
chỉ tune một scene khi `VideoCompiler` sắp thật sự encode nó.

**I/O đĩa của probe chiếm khoảng 75% wall time mỗi probe** (đo trên 52 probe thật, không phải ước
lượng): mỗi probe ghi PNG thật ra một thư mục tạm chỉ để biết kích thước bytes rồi đọc lại để so PSNR,
tức thuần overhead. Đã sửa bằng chế độ `AssetStore` in-memory (vẫn encode PNG, nhưng vào `MemoryStream`
thay vì file) và một renderer đọc thẳng asset probe từ đó. Kiểm tra regression trên cùng scene xác
nhận output trùng byte và tuning nhanh hơn khoảng 2,4 lần trên một cặp scene thật (213 giây xuống 88
giây).

**Chia ba hệ thống, detection giới hạn theo window, asset store toàn cục.** Cấu trúc phẳng
`Analysis`/`Encoder`/`Media`/`Osb`/`Render`/`Evaluation`/`Tuning` của `Compiler` được gom lại thành 3
thư mục hệ thống (`Detection`, `Tuning`, `Encode`) cộng `Shared` và `Compilation`. Thuần di chuyển và
đổi namespace, không đổi hành vi, xác minh bằng cách grep toàn bộ `using` và tham chiếu đủ điều kiện
trước khi di chuyển.

Cùng lượt đó có một thay đổi hành vi: `ScenePrePass.ScanAsync` trước kia decode toàn bộ file nguồn để
tìm mọi cut, dù một lần compile `.osbv` chỉ cần các scene giao với cửa sổ của chính nó. Đã giới hạn
`ScanAsync` vào `[windowStartMs, windowEndMs)`.

`VideoSourcePlan` từng mang `VideoId` đặt tên thư mục con của asset store, mỗi plan một instance
`AssetStore`. Điều đó chặn hai cơ hội dedupe thật: cùng một file encode lại ở fps khác, và hai file
*khác nhau* có tile sinh ra nội dung trùng byte. Đã sửa triệt để hơn một `VideoId` tốt hơn: bỏ hẳn
trường đó. Một instance `AssetStore` phẳng, content-addressed (`s/{hash}.png`, `a/{hash}/f{n}.png`)
dùng chung cho *cả* compile. Phạm vi đã chốt: chỉ dedupe theo hash chính xác, không so khớp gần đúng
hay theo cảm nhận, không nội suy frame giữa các fps để tạo thêm hash hit. `AssetStore` phải được dựng
một lần mỗi lần gọi `CompileAsync` và truyền xuống, không phải mỗi plan một cái, vì `FileCount` và
`TotalBytes` sẽ đếm trùng một cache hit xuyên plan mà instance kia không biết.

**Phương pháp mẫu train và eval cho tuning.** Thiết kế ban đầu probe một cửa sổ mẫu khoảng 1,5 giây
cho mỗi candidate, nên một candidate có thể trông như thắng thuần túy vì nội dung mà đúng đoạn clip đó
chứa. Thiết kế mới, chốt qua đối thoại: mục đích của eval là *từ chối* chứ không chỉ đo, nên một
candidate phải vượt sàn PSNR trên *cả* probe train lẫn một probe eval mà nó chưa từng thấy khi chọn;
mẫu trải đều theo thời gian; kích thước ban đầu là 3 đoạn train cộng 1 đoạn eval, mỗi đoạn 500ms.

Một bug tìm ra qua regression thật (`badapple8`), unit test không bắt được: bản cài đặt đầu tiên trải
3 cửa sổ train và 1 cửa sổ eval ra 4 phần *tách rời, cách xa nhau* của scene. Trên fixture bad_apple
thật, cách này đo baseline PSNR ở 28,02dB thay vì baseline một mẫu đã biết khoảng 24,90dB, vì lát eval
xa rơi vào nội dung dễ nén hơn, đẩy sàn tự hiệu chuẩn lên khoảng 3dB và khiến mọi trục rơi về baseline.
Hậu quả: tổng bytes tăng 18,9% (34,3MB so với 28,9MB đã biết là tốt) và thời gian tuning gấp khoảng 2
lần. Nguyên nhân gốc: một phép chia train/eval mà các mẫu rải khắp scene đang đo "combo này tổng quát
hóa tốt tới đâu trên toàn scene", một câu hỏi khác và chặt hơn "một mẫu 3 giây cục bộ có đại diện
không".

Đã sửa bằng cách thay bố cục bốn phần tư rải rác bằng một *khối cục bộ* `RequiredSampleMs=3000ms` (căn
giữa nếu là segment đầu tiên được tune, neo ở đầu segment nếu không), chia thành 4 khối liền kề 750ms,
lấy khối 0, 1, 3 làm train và khối 2 làm eval. Xác minh trên cùng fixture thật (`badapple9`): output
trùng byte với baseline tốt trước khi regression (28.793.996 bytes tổng), cùng combo được chọn cho mỗi
scene.

Một tinh chỉnh cuối, từ nhận xét của người dùng: một scene không dài hơn `RequiredSampleMs` không phải
là mẫu của cái gì lớn hơn, nó *chính là* toàn bộ sản phẩm cho scene đó, nên tune nó với một lát eval
giữ lại của chính nó chẳng đạt được gì (không còn "vật liệu chưa thấy" nào để tổng quát hóa tới, và
overfit vào 100% dữ liệu output của chính mình là mục tiêu chứ không phải rủi ro). `BuildSampleWindows`
giờ rẽ nhánh: segment ngắn hơn hoặc bằng `RequiredSampleMs` trả về toàn bộ span của nó làm cả train
lẫn eval (probe một lần, dùng lại kết quả), segment dài hơn giữ nguyên cách chia 4 khối. Xác minh trên
`badapple10`: với scene ngắn 2083ms trong fixture đó, mọi dòng probe in `trainPSNR` bằng đúng
`evalPSNR`, xác nhận là dùng lại chứ không phải probe lần hai; scene dài hơn vẫn chia bình thường; các
số đếm object (`sprites=6897 animations=1467 commands=8364 assets=27706`) khớp baseline tốt đã biết ở
mọi chiều.

**Decode dùng chung cho các cửa sổ mẫu, và bỏ qua vòng PNG trong probe.** `ParameterTuner.TuneAsync`
decode trước các cửa sổ mẫu cố định đúng một lần rồi replay qua `TileEncodeLoop.Options.PreDecodedFrames`.
Số lần spawn ffmpeg mỗi lượt tuning: 40 xuống 4. Thời gian chờ frame: 54,9 giây xuống 13ms. Bản
in-memory của `AssetStore` giữ pixel sau lượng tử hóa (`GetMemoryPixels`) và
`SoftwareStoryboardRenderer.LoadAsset` đọc thẳng từ đó, nên vòng decode PNG biến mất khỏi đường probe.
Đường compile ghi đĩa không đổi.

Một cảnh báo về tính đúng đắn mà bản audit phát hiện, vì tuyên bố ban đầu quá mạnh: output *không*
trùng byte so với đường cũ, và bản thân đường cũ cũng chưa bao giờ có tính chất đó, vì decode theo
từng lần spawn ffmpeg dao động ±1 frame ở biên cửa sổ. Trong một lần chạy đường *cũ*, cùng một bộ
(64,32,8,0) được probe ở hai trục khác nhau cho ra chi phí lệch **9%** (3.226.823 so với 3.530.775) và
eval PSNR lệch 0,01dB. Đường mới so đường cũ giữa các lần chạy: train 29,53 so với 29,56dB, eval 23,76
so với 23,67dB, chi phí lệch trong 6,5%, cùng bậc với dao động vốn có đó. Quyết định giữ nguyên
(64/32/8/0 ở cả hai đường). Đường dùng chung *loại bỏ* dao động trong một lần chạy, vì một lần decode
mỗi cửa sổ nghĩa là mọi candidate so với cùng một bộ frame tham chiếu cố định.

Một bug thật xuất hiện khi bản dùng chung được viết lại bằng fan-out theo channel
(`WindowFrameSource`, `FanOutFrameBuffer`): producer ghi tới *mọi* consumer vô điều kiện trên mỗi
frame, nên bất kỳ consumer nào mà probe không đang đọc sẽ đầy buffer 8 frame và **chặn producer**,
khiến nó không thể nuôi các consumer đang được đọc, tức **deadlock ở concurrency từ 4 trở lên**. Ở
concurrency 1 thì không deadlock nhưng **giao thiếu frame** (chỉ khoảng 8 frame đã buffer tới được
reader duy nhất), nên probe render vài frame rồi báo "thành công" nhanh giả tạo khoảng 56 giây kèm một
PSNR trông hợp lý, tức công việc dở dang giả dạng thành thắng lợi. Đã sửa bằng cách quay lại bản replay
in-memory đã được chứng minh (commit `8de86aa`): decode mỗi cửa sổ mẫu *một lần* vào `List<byte[]>` rồi
replay qua `PreDecodedFrames`, không có channel nào để deadlock. Xác minh chạy được ở cả concurrency 1
và 4, báo 4 cửa sổ đã decode, RAM đỉnh 1,96 GB, combo 64/32/8/0.

A/B trên máy có GPU decode (CUDA), bad_apple scene 5 giây ở giữa, concurrency 1:

| | đường cũ (`--no-shared`) | đường mới (dùng chung) | thay đổi |
| --- | ---: | ---: | --- |
| Số lần spawn ffmpeg | 40 | 4 | ít hơn 10 lần |
| Chờ frame (ffmpeg) | 43.239ms (9,7%) | 12ms (0,0%) | loại bỏ |
| Công CPU khi encode | 444.300ms | 351.050ms | giảm 21% |
| Wall time | 358.129ms | 360.881ms | ngang nhau (+0,8%) |
| RAM đỉnh | 1,71 GB | 1,96 GB | tăng 0,25 GB (giữ frame đã decode) |
| Combo | 64/32/8/0 | 64/32/8/0 | ổn định |

Trên một máy khác chỉ decode bằng CPU, cùng code và cùng thiết kế, wall time của đường mới *tệ hơn*
(526 so với 336 giây). Khác biệt nằm ở đường decode của máy: với decode CPU, đường cũ chạy 40 lần
decode ffmpeg *bất đồng bộ* phía sau phần việc CPU nên mua được khoảng 2,5 lần chồng lấn pipeline, còn
đường mới thuần CPU sau lần decode một lần nên chỉ có khoảng 1,0 lần chồng lấn. Với GPU decode, lợi
thế chồng lấn đó biến mất và cả hai đều bị chặn bởi CPU của software renderer. Kết luận: khoảng cách
wall time là do máy chứ không phải regression của code; số đo độc lập với tải là tổng công CPU, và nó
giảm một nửa.

**Số liệu của một lần compile đầy đủ.** Minecraft full clip 60fps, 1920x804, decode bằng RTX 3060: 542
phút (khoảng 9 tiếng), `.osb` 9,31 MB cộng asset 735,01 MB, tổng 744,32 MB, 79.612 sprite và 79.223
asset, 36 scene, 35 cut, timeline 60fps nhân bản từ nguồn 23,976fps. Đây là con số nền để so mọi cải
tiến sau này.
