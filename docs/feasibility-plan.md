# OsbMpeg: kế hoạch kiểm chứng trước triển khai (feasibility)

Trạng thái: chờ người dùng duyệt. Ngày: 2026-09-29.

Tài liệu này mô tả công việc **trước** `docs/implementation-plan.md`. Mục đích không phải xây sản phẩm,
mà là tạo bằng chứng cho hai câu hỏi:

1. **Hướng đi có đúng không?** Closed loop với sàn chất lượng tuyệt đối có thật sự chữa được bệnh mờ,
   với chi phí dung lượng chấp nhận được, và có tốt hơn các phương án đơn giản hơn không?
2. **Hướng đi có triển khai được không?** Các giả định kỹ thuật (osu!lazer, OsuParsers, ffmpeg, hệ
   thống file, hiệu năng) có đứng vững không?

Kết quả cuối là báo cáo `docs/feasibility.md` với kết luận **go**, **go có điều chỉnh** hoặc **no-go**,
cùng danh sách thay đổi cần áp vào `implementation-plan.md` trước khi bắt đầu Phase A.

---

## 0. Nguyên tắc bắt buộc

- **Không viết code sản phẩm.** Mọi code nằm trong một project spike ở worktree riêng, trên nhánh
  `spike/feasibility`. Nhánh này **không bao giờ được merge** vào `main` và không push. Thứ được giữ lại
  chỉ là số liệu, ảnh và báo cáo.
- **Tiêu chí pass/fail đã chốt ở mục 2, trước khi chạy.** Không được sửa tiêu chí sau khi đã thấy kết
  quả. Nếu phát hiện một tiêu chí vô nghĩa (ví dụ đo sai thứ), ghi thành "amendment" ở mục 6 của báo cáo
  kèm lý do, trình người dùng, rồi mới chạy lại.
- **Không tinh chỉnh để cho đẹp.** Thuật toán của simulator bám đúng mô tả ở Phụ lục A. Không thêm
  heuristic nào không có trong spec chỉ để số liệu đẹp hơn.
- **Trên `main`** chỉ được tạo tag `v1-baseline` và file báo cáo `docs/feasibility.md` (commit sau khi
  người dùng xem). Không sửa gì khác.
- **Chỗ phải dừng chờ người dùng:** gói review (công việc U1, gồm V4, V7 và V8) và kết luận V11. Ngoài
  ra, gặp điều spec không nói và không suy ra được thì dừng lại hỏi, không tự quyết.
- **Tra cứu thư viện** bằng context7 hoặc deepwiki. Không dịch ngược DLL.

---

## 1. Giả thuyết cần chứng minh

| ID | Giả thuyết | Nếu sai thì | Kiểm bằng |
| --- | --- | --- | --- |
| H1 | Mờ ở v1 chủ yếu do **stale**: vòng hở giữ tile cũ quá lâu. Mất mát do encode (quantize, hash tolerance) là thứ yếu. | Closed loop vẫn chữa được (patch lossless), nhưng lý do để đầu tư vào vòng lặp yếu đi; sửa encoding có thể là đòn bẩy rẻ hơn. | V1 |
| H2 | Sàn tuyệt đối từng frame đạt được với dung lượng chấp nhận được. | Hướng closed loop với sàn cứng không dùng được như dự kiến. | V2 |
| H3 | Ở cùng chất lượng, closed loop rẻ hơn v1. Ở chất lượng cao, closed loop rẻ hơn phương án đơn giản "v1 với tham số chặt" (hash chính xác, tolerance 0, không quantize). | Nếu v1 chặt rẻ hơn thì closed loop là thừa: chỉ cần chỉnh tham số v1. | V3 |
| H4 | Có mức sàn mà mắt người thấy hết mờ so với v1. | Metric không khớp cảm nhận, phải đổi metric hoặc preset. | V4 |
| H5 | Photometric (lệnh `C`) giảm asset trên fade. **Crossfade nhân quả gần như không giảm asset**: B = frame hiện tại cho ra số lần vi phạm y như hard swap. Crossfade chỉ có lợi khi có **lookahead** (chọn B ở tương lai). Lookahead cũng giúp patch thường chọn nội dung "giữa" để sống lâu hơn. | M1 như đang viết sẽ trượt acceptance 2 (dissolve ≤ 1/5 asset). Phải đưa lookahead vào M1, hoặc bỏ crossfade khỏi M1. | V5 |
| H6 | Disjoint mode (lưới 128 px khi group có fade hoặc additive) không làm chi phí tăng quá nhiều. | Phải thiết kế lại disjoint mode, ví dụ chỉ bật trong khoảng thời gian group alpha < 1. | V6 |
| H7 | osu!lazer hiển thị đúng như giả định: giữ giá trị trong gap, S nhân V, blend straight alpha, tint nhân. Patch ghép sát nhau **không lộ đường nối** khi lazer scale theo độ phân giải màn hình (thường là tỉ lệ không nguyên). Chuỗi patch 60fps không nháy đen. | Sai về ngữ nghĩa thì phải sửa renderer và evaluator theo lazer. Lộ đường nối thì cần overlap hoặc bleed ở mép patch: thay đổi lớn cho DirtyRects và cho asset. | V7 |
| H8 | Output kiểu closed loop chạy mượt trong lazer, không tệ hơn v1. | Cần ngân sách workload (M3) ngay trong M1. | V8 |
| H9 | Hiệu năng (ErrorMap, PNG, preview, verify) nằm trong ngân sách thời gian. | Phải tối ưu hoặc đổi thiết kế đánh giá trước M1. | V9 |
| H10 | Giả định kỹ thuật đứng: API của OsuParsers 1.7.2, ngữ nghĩa resample fps của ffmpeg, ghi file nguyên tử đa tiến trình trên Windows. | Sửa các phần tương ứng trong implementation plan. | V10 |

---

## 2. Tiêu chí pass/fail (chốt trước khi chạy)

Người dùng được quyền sửa bất kỳ ngưỡng nào dưới đây **trước khi** agent bắt đầu chạy. Sau đó thì khóa.

Quy ước dùng trong bảng:

- Fixture được gọi theo ID trong `tests/fixtures/README.md`.
- "Nội dung sạch" = cả nhóm `easy/*`, cộng `medium/fish-short` và `hard/minecraft`.
- "Nội dung nhiễu" = `medium/badapple` và `hard/birdbrain`.
- `medium/fish`, `hard/machinelove` và `hard/rollback` được báo cáo đầy đủ, nhưng chỉ tính vào tiêu chí
  nào ghi rõ tên chúng hoặc ghi "mọi fixture".
- Mọi số đo đều lấy trên **clip dẫn xuất** (mục 3.3), không lấy trên video gốc.
- Sàn tạm dùng trong spike: Low = 30 dB / 0,80; Medium = 34 dB / 0,88; High = 38 dB / 0,92. Cặp số là
  (PSNR tối thiểu mỗi frame, TileSsim tối thiểu).
- Bytes = tổng byte PNG của các asset *khác nhau* (content-addressed), cộng byte `.osb` ước tính theo
  Phụ lục A.6.

| ID | Tiêu chí | Pass khi |
| --- | --- | --- |
| G1 | Chẩn đoán v1 (H1) | Ở sàn Medium, tile-frame vi phạm thuộc loại `stale` chiếm ≥ 50% trên ít nhất 60% số fixture được tính. Fixture có dưới 100 tile-frame vi phạm thì không tính, vì tỉ lệ không có nghĩa. |
| G2 | Chi phí sàn (H2) | Ở Medium, bytes của simulator (S0) trên **nội dung sạch** ≤ 2,0× bytes của v1-baseline. Nội dung nhiễu không có gate, chỉ báo cáo. Nhưng phải tồn tại một mức sàn trong lưới quét mà bytes ≤ 5× v1, để còn đường lùi. |
| G3a | Cùng chất lượng (H3) | Với sàn = p1 PSNR và p1 TileSsim mà V1 đo trên chính output v1, bytes simulator ≤ 1,0× v1 trên ít nhất 60% số fixture. |
| G3b | Hơn phương án đơn giản (H3) | Ở High, bytes simulator < bytes của v1-strict trên **mọi** fixture. |
| G4 | Mắt người (H4) | Người dùng chỉ ra, trên từng fixture sạch, một mức sàn mà họ thấy hết mờ so với v1. Mức đó phải có bytes thỏa G2 (≤ 2,0× v1). |
| G5a | Photometric (H5) | Trên `fade_black`, biến thể P giảm ≥ 90% số asset mới trong đoạn fade so với S0. |
| G5b | Crossfade nhân quả (H5) | Trên `dissolve`, biến thể C giảm < 20% asset so với S0. Đây là **xác nhận lý thuyết**; nếu C giảm ≥ 20% thì giả thuyết nhân quả sai, và báo cáo phải giải thích vì sao. |
| G5c | Lookahead (H5) | Trên `dissolve`, biến thể CL giảm ≥ 80% asset so với S0. |
| G6 | Disjoint mode (H6) | Ở Medium, bytes khi rect bắt theo lưới 128 px ≤ 1,3× bytes khi rect tự do, trên mọi fixture. |
| G7 | lazer (H7) | Mọi case ở Phụ lục C đạt: probe màu lệch ≤ 6/255 mỗi kênh; K0 thấy sprite; K1 cho kích thước khớp phép nhân; K6 không có đường nối nhìn thấy, cả khi người dùng quan sát lẫn khi so số với K7 (không có cột hay hàng pixel nào lệch > 24/255 dọc theo mép patch trên ảnh chụp); K9 không nháy đen. |
| G8 | Tải trong lazer (H8) | Pack simulator có FPS trung bình không thấp hơn pack v1 quá 10%, không giật thấy rõ, thời gian load ≤ 2× pack v1. |
| G9 | Hiệu năng (H9) | Trên clip `standard` của `medium/fish` và `hard/minecraft`, thời gian dự phóng (encode + verify) ≤ 3× wall time đo được của v1 trên cùng clip. `hard/minecraft` full length, độ phân giải và fps gốc, dự phóng theo Phụ lục A.8 ≤ 542 phút. |
| G10 | Kỹ thuật (H10) | (a) Mọi điểm probe của OsuParsers khớp, hoặc đã có fallback ghi trong implementation plan. (b) Có một giá trị `round` cho 0 mismatch ở mọi phép đổi fps. (c) 5 vòng ghi đa tiến trình: 0 file hỏng, 0 ghi đè, 0 file tạm còn sót (trừ vòng cố ý kill), 0 lỗi ngoài dự kiến. |

**Quy tắc kết luận** (V11):

- **Go:** G2, G3a, G3b, G4, G7 và G10 đều pass, và các tiêu chí còn lại pass hoặc có biện pháp giảm
  thiểu đã nêu ở cột "Nếu sai" của mục 1.
- **Go có điều chỉnh:** tiêu chí nào fail mà biện pháp đã rõ (ví dụ G5c pass nhưng G5b cũng xác nhận
  crossfade nhân quả vô dụng: đưa lookahead vào M1; G6 fail: thiết kế lại disjoint). Báo cáo phải liệt
  kê từng thay đổi cụ thể cho `implementation-plan.md`.
- **No-go:** G2 fail trên nội dung sạch ở mọi mức sàn; hoặc G3b fail (v1 chặt rẻ hơn); hoặc G7 lộ đường
  nối mà không có biện pháp khả thi; hoặc G4: người dùng không thấy mức sàn nào đạt. Khi đó báo cáo đề
  xuất hướng thay thế, ví dụ cải tiến v1 (dither, tham số chặt) thay cho closed loop.

---

## 3. Môi trường và quy ước

### 3.1 Worktree và project spike

```text
git tag v1-baseline                                          # trên commit đã có bộ fixture mới và tests/fixtures/README.md
                                                             # (code v1 không đổi so với fac691a)
git worktree add -b spike/feasibility ../OsbMpeg-feasibility v1-baseline
```

Trong worktree, tạo project console `spikes/OsbMpeg.Feasibility/OsbMpeg.Feasibility.csproj`:

- `net10.0`, `OutputType=Exe`, `Nullable` và `ImplicitUsings` bật.
- `ProjectReference` tới `../../src/OsbMpeg.Compiler/OsbMpeg.Compiler.csproj`, để dùng lại
  `FrameSource`, `MediaProbe`, `OsbReader`, `LoopFlattener`, `SoftwareStoryboardRenderer`, `Metrics` và
  ImageSharp.
- `PackageReference` tường minh `OsuParsers` 1.7.2, cho V10a.
- Không thêm dependency nào khác. Nếu thật sự cần một thứ ngoài danh sách thì dừng lại hỏi.

Cấu trúc:

```text
spikes/OsbMpeg.Feasibility/
  Program.cs                 dispatch subcommand theo args[0] (switch đơn giản, không dùng Spectre)
  Common/
    Paths.cs                 thư mục output, catalog fixture
    FixtureCatalog.cs        catalog và profile theo tests/fixtures/README.md, cộng synthetic (mục 3.3)
    ClipMaker.cs             sinh và cache clip dẫn xuất (mục 3.3)
    Metrics2.cs              Luma int, BlockMse, TileSsim (Phụ lục A.1)
    ErrorMap.cs              Phụ lục A.2
    DirtyRects.cs            Phụ lục A.3
    Ownership.cs             Phụ lục A.4
    PngCost.cs               encode PNG vào MemoryStream + dedupe theo key (Phụ lục A.5)
    Rational.cs              phân số cho V10b
    Markdown.cs              ghi bảng .md
    RawFfmpeg.cs             gọi ffmpeg trực tiếp, đọc rawvideo từ stdout (V10b)
  Experiments/
    Baseline.cs  ProbeOsuParsers.cs  ProbeFps.cs  StorageRace.cs  DiagnoseV1.cs
    Simulator.cs  SimulatorX.cs  Perf.cs  LazerPack.cs  WorkloadPack.cs  Gallery.cs  SelfTest.cs
```

Build và chạy (trong worktree):

```text
dotnet build -c Release spikes/OsbMpeg.Feasibility
spikes/OsbMpeg.Feasibility/bin/Release/net10.0/OsbMpeg.Feasibility.exe <subcommand> [args]
```

Commit trên `spike/feasibility` sau mỗi công việc (message dạng `spike: V2 simulator`) chỉ để giữ lịch
sử. Không push.

### 3.2 Thư mục output

Mọi output ghi vào `%TEMP%\osbmpeg-feasibility\` (sau đây gọi là `OUT`), mỗi công việc một thư mục con
(`OUT\v0`, `OUT\v1`, …). Mỗi công việc ghi `OUT\<vX>\summary.md`, là nguồn số liệu cho báo cáo. Không
ghi output vào repo.

### 3.3 Fixture

**Video thật.** Catalog, profile, quy tắc chọn cửa sổ và lệnh sinh clip nằm **duy nhất** ở
`tests/fixtures/README.md`. Mục này chỉ nói spike dùng chúng thế nào.

- **Quy tắc bắt buộc:** medium và hard **không bao giờ** chạy ở độ dài, độ phân giải và fps gốc, trừ khi
  người dùng yêu cầu rõ. Mục đích là không decode, render và đo một đống thông tin thừa. Chúng chạy trên
  clip dẫn xuất: cửa sổ ngắn, scale xuống, cap fps.
- Clip dẫn xuất là **nguồn** cho mọi phương án được so sánh: v1 (V0, V3b), simulator (V2, V5), đo chất
  lượng (V1). Nhờ vậy A/B công bằng.
- **Profile theo công việc:**

  | Công việc | easy | medium, hard |
  | --- | --- | --- |
  | V0, V1, V2, V3, V6, V9 (đo) | `native` | `standard` (10 s, cao 720, fps ≤ 30) |
  | V5 | `native` | `standard`, nhưng decode ở chiều cao 360 qua `FrameSource` (không sinh clip mới) |
  | V8 | — | `hard/minecraft` ở `standard` |
  | V9 dự phóng | — | chỉ **decode** 10 s gốc của `medium/fish` và `hard/minecraft` để đo tốc độ decode native; không encode, không render |
  | Thử nhanh trong lúc viết code spike | `native` | `quick` (3 s, cao 360, fps ≤ 24) |

- **Sinh clip:** subcommand `make-clips <profile>` sinh clip cho mọi fixture medium và hard theo lệnh ở
  `tests/fixtures/README.md`, vào cache `%TEMP%\osbmpeg-clips\`. `make-clips` chạy **sau V10b**, để dùng
  đúng giá trị `round` mà V10b chọn.
- Cửa sổ được chọn theo quy tắc trong README và được ghi vào manifest của clip. Bảng cửa sổ trong
  `tests/fixtures/README.md` được điền và commit **cùng lúc với báo cáo** ở V11; đây là ngoại lệ duy nhất
  cho phép sửa file ngoài báo cáo trên `main`.
- **ffmpeg:** ffmpeg và ffprobe phải nằm trên `PATH`, hoặc spike đọc biến môi trường
  `OSBMPEG_FFMPEG_DIR`. Trên máy dev hiện tại, bản duy nhất tìm thấy là bản đi kèm Krita
  (`C:\Program Files\Krita (x64)\bin`), không nằm trên `PATH`. Spike truyền thư mục đó vào
  `FFOptions.BinaryFolder` khi gọi FFMpegCore, và gọi thẳng `ffmpeg.exe` trong thư mục đó khi dùng
  `Process`.
- `easy/ball` là GIF (8 frame). Nếu v1 hoặc `FrameSource` không đọc được, chuẩn hóa một lần theo README
  (lossless, `yuv444p`).

**Synthetic.** Subcommand `make-synthetic` tạo vào `OUT\synthetic\`. Lệnh ffmpeg giữ nguyên văn:

```text
static.mp4       ffmpeg -y -f lavfi -i "smptehdbars=size=1280x720:rate=30" -t 2 -c:v libx264 -crf 12 -pix_fmt yuv420p static.mp4
dissolve.mp4     ffmpeg -y -f lavfi -i "smptehdbars=size=1280x720:rate=30:duration=4" -f lavfi -i "rgbtestsrc=size=1280x720:rate=30:duration=4" -filter_complex "[0][1]xfade=transition=fade:duration=2:offset=1,format=yuv420p" -c:v libx264 -crf 12 dissolve.mp4
fade_black.mp4   ffmpeg -y -f lavfi -i "smptehdbars=size=1280x720:rate=30:duration=4" -vf "fade=t=out:st=1:d=2,format=yuv420p" -c:v libx264 -crf 12 fade_black.mp4
tint.mp4         ffmpeg -y -f lavfi -i "smptehdbars=size=1280x720:rate=30:duration=4" -vf "format=rgb24,geq=r='r(X,Y)':g='g(X,Y)*max(0\,1-T/3)':b='b(X,Y)*max(0\,1-T/3)',format=yuv420p" -c:v libx264 -crf 12 tint.mp4
counter60.mkv    ffmpeg -y -f lavfi -i "nullsrc=s=64x64:r=60:d=2,format=rgb24,geq=r='N*2':g='N*2':b='N*2'" -c:v png counter60.mkv
counter24.mkv    ffmpeg -y -f lavfi -i "nullsrc=s=64x64:r=24:d=5,format=rgb24,geq=r='N*2':g='N*2':b='N*2'" -c:v png counter24.mkv
counter2397.mkv  ffmpeg -y -f lavfi -i "nullsrc=s=64x64:r=24000/1001:d=5,format=rgb24,geq=r='N*2':g='N*2':b='N*2'" -c:v png counter2397.mkv
```

Chạy xong, kiểm từng file bằng ffprobe: đúng kích thước, fps và thời lượng. Với file `counter*`, kiểm
thêm frame N có giá trị pixel đúng bằng 2N (decode lossless).

### 3.4 Định nghĩa đo

Toàn bộ định nghĩa đo (luma, PSNR, TileSsim, ErrorMap, DirtyRects, ownership, bytes) nằm ở Phụ lục A.
Mọi công việc phải dùng đúng các định nghĩa đó, không viết biến thể riêng.

---

## 4. Công việc

### F0. Chuẩn bị

1. Tạo tag và worktree theo mục 3.1.
2. Tạo khung project spike, `Program.cs` có dispatch, và `self-test`.
3. `self-test` chạy các kiểm tra nhỏ bằng `Debug.Assert`/exception cho Metrics2, ErrorMap, DirtyRects
   và Ownership, theo đúng các test mô tả ở Phụ lục A. In "OK" hoặc lỗi.
4. `make-synthetic` (mục 3.3).
5. Xác định vị trí ffmpeg (mục 3.3). `ffmpeg -version` phải chạy được qua đường đó.

Xong khi `self-test` in OK và 7 file synthetic đã qua kiểm tra. `make-clips` **không** nằm trong F0; nó
chạy ngay sau V10b.

### V0. Baseline v1

**Mục đích:** có mốc so sánh cho mọi công việc sau.

Subcommand: `baseline`. Chạy cho mọi fixture: easy dùng file gốc, còn medium và hard dùng clip
`standard` (mục 3.3). Vì vậy V0 cần `make-clips standard` xong trước.

1. Tạo `OUT\v0\<fixture>\in.osbv` gồm một dòng
   `AnimationVideo,Background,Centre,"<đường dẫn tuyệt đối tới clip hoặc file easy>",320,240,0`. Dòng này
   nghĩa là: tâm (320, 240), bắt đầu tại 0 ms, fps nguồn của clip, toàn bộ clip. Nếu parser từ chối thì
   đọc `OsbvParserTests` ở tag để lấy đúng cú pháp. Nếu v1 cần ffmpeg ngoài `PATH`, thêm thư mục ffmpeg
   vào `PATH` của tiến trình con (v1 không có tham số chọn thư mục ffmpeg cho `compile`).
2. Xóa `OUT\v0\<fixture>\` nếu đã có, để đo lúc cache lạnh (asset store của v1 bỏ qua file đã tồn tại).
3. Chạy trực tiếp exe CLI của v1: `src/OsbMpeg.Cli/bin/Release/net10.0/osbmpeg.exe in.osbv
   OUT\v0\<fixture>\storyboard.osb OUT\v0\<fixture>\sb`. Khởi chạy bằng `Process` từ spike: đo wall time
   bằng `Stopwatch`, lấy mẫu `PeakWorkingSet64` mỗi 500 ms, lưu stdout và stderr vào `log.txt`.
4. Ghi các số sau:
   - wall time, RAM đỉnh;
   - byte của `.osb`, số dòng `.osb`;
   - số file và tổng byte dưới `sb\`;
   - `sprites`, `animations`, `commands` (parse từ dòng `sprites=… animations=…` mà CLI in ra).

Output: `OUT\v0\summary.md` (một dòng mỗi fixture). **Giữ nguyên** thư mục output, vì V1, V3 và V8 dùng
lại.

Lưu ý: mỗi lần chạy v1 tự tune tham số, có thể mất vài phút cho mỗi fixture. Chạy nền trong lúc làm
V10a và V10c.

### V10. Probe kỹ thuật (ba việc độc lập, làm song song với V0)

**V10a. OsuParsers 1.7.2.** Subcommand: `probe-osuparsers`. Kiểm bằng code biên dịch được và reflection,
rồi ghi bảng "điểm | kỳ vọng | thực tế | khớp?":

1. `Command.StartTime` và `EndTime` có kiểu `int`.
2. Danh sách constructor public của `Command`, `LoopCommand`, `TriggerCommand` khớp Phụ lục A của
   `implementation-plan.md`.
3. `new Storyboard()` và `new CommandGroup()` biên dịch được.
4. `Storyboard.GetLayer(StoryboardLayer)` tồn tại.
5. Có `StoryboardDecoder.Decode(IEnumerable<string>)` và `Decode(Stream)`. Gọi thử với 5 dòng `.osb`, số
   object phải đúng.
6. Kiểu phần tử của `SamplesLayer`, cùng tên và kiểu các property của nó.
7. Có type public nào tên `StoryboardEncoder` không? Namespace là gì, chữ ký `Encode` ra sao?
8. **Round-trip:** dựng một `Storyboard` gồm một sprite có F, M, V, C, P, một loop và một trigger.
   `Save` ra file tạm, dưới `CultureInfo.CurrentCulture = vi-VN`, rồi `Decode` lại. Mọi giá trị phải khớp,
   và text không được có dấu phẩy thập phân.

Output: `OUT\v10\osuparsers.md`.

**V10b. Ngữ nghĩa resample fps của ffmpeg.** Subcommand: `probe-fps`.

Với mỗi tổ hợp:

- nguồn/đích: counter60 → 30, counter60 → 24, counter24 → 60, counter2397 → 30, counter60 → 60;
- `From` ∈ {0, 0,5 s}, dùng input seek `-ss` đặt trước `-i`, đúng như `FrameSource` làm;
- `round` ∈ {zero, inf, down, up, near}.

Gọi ffmpeg trực tiếp:
`ffmpeg -ss <From> -i <file> -vf "fps=fps=<đích>:round=<r>" -f rawvideo -pix_fmt rgb24 -`, đọc stdout,
lấy giá trị pixel (0,0) chia 2 để ra chỉ số frame nguồn N thực nhận.

Kỳ vọng theo ngữ nghĩa D7: output i phải là frame nguồn mới nhất có `pts ≤ From + i/fps_out`, tức
`N_exp(i) = floor((From + i·den_out/num_out) · num_in/den_in)`. Tính bằng số hữu tỉ chính xác
(`Rational` trên `BigInteger` hoặc `long`), vì 24000/1001 không biểu diễn được chính xác bằng double.

Ghi bảng số mismatch cho từng tổ hợp × `round`, cộng 10 mismatch đầu tiên của mỗi ô (i, N thực, N kỳ
vọng). Kết luận giá trị `round` nào cho 0 mismatch ở mọi tổ hợp. Nếu không có giá trị nào, mô tả quy luật
lệch và đề xuất cách thay thế (ví dụ decode ở fps nguồn rồi tự chọn frame trong C#), kèm cái giá của cách
đó (luồng pipe tăng, VFR không có pts).

Output: `OUT\v10\fps.md`.

**V10c. Ghi file nguyên tử đa tiến trình trên Windows.** Subcommand `storage-race` (điều phối) và
`storage-worker` (tiến trình con, chính exe spike).

- `FsAtomicStore` hiện thực **đúng nguyên văn** thuật toán ở mục 5.2 của `implementation-plan.md`: file
  tạm `.osbmpeg-{guid}.tmp` cùng thư mục, `Flush(true)`, `File.Move(overwrite:false)`, retry 5 lần với
  backoff 50·2^k ms, dọn file tạm trong `finally`, và sweep file tạm cũ (ngưỡng tuổi là tham số).
- Tập key: 200 key. Nội dung mỗi key là tất định: độ dài từ 32 KB đến 2 MB, byte sinh bằng
  `new Random(seed = hash(key))`. Đường dẫn `race/s/{key}.bin`.
- **Mỗi vòng:**
  1. Thư mục trống mới.
  2. Khởi chạy **8 tiến trình writer**. Mỗi writer ghi toàn bộ 200 key theo thứ tự xáo trộn bằng seed
     riêng, rồi in JSON gồm số lần `created`, số lần `existed`, số retry và lỗi.
  3. Chạy đồng thời **2 tiến trình reader**. Trong 20 giây, mỗi reader liên tục chọn key ngẫu nhiên; nếu
     file tồn tại thì đọc và so với nội dung kỳ vọng, rồi in số lần đọc, số mismatch và số lỗi IO.
  4. Kiểm sau vòng:
     - mọi writer thoát với mã 0;
     - Σ `created` = 200 đúng;
     - mọi file cuối có nội dung đúng;
     - không còn `.tmp`;
     - reader có 0 mismatch.
- Chạy 5 vòng bình thường. Thêm **vòng 6:** kill một writer sau 1 giây. Kiểm: file cuối vẫn đúng (các
  writer khác ghi bù phần thiếu); có thể còn `.tmp` mồ côi; gọi sweep với ngưỡng 0 giây thì các `.tmp`
  bị dọn sạch.
- Chạy với Windows Defender ở trạng thái mặc định của máy. Ghi lại trạng thái đó trong báo cáo.

Output: `OUT\v10\storage.md`.

### V1. Chẩn đoán vì sao v1 mờ

**Mục đích:** H1 và G1. Cung cấp p1 PSNR và p1 TileSsim của v1 cho G3a, và ảnh render v1 cho V4.

Subcommand: `diagnose-v1 <fixture>`. Chạy cho mọi fixture, trên output của V0 (nguồn = clip hoặc file
easy mà V0 đã dùng). Chạy thêm trên output
v1-strict của V3b (khi có) để đo chất lượng của nó.

1. Đọc `.osb` bằng `OsbReader.Read`. Với mỗi object, thay `Commands` bằng
   `LoopFlattener.Flatten(Commands)`: renderer v1 bỏ qua children của loop, nên phải flatten trước.
   Output v1 mỗi sprite chỉ có một lệnh `S`, nên bug khoảng trống của evaluator không ảnh hưởng. Ghi rõ
   lập luận này trong summary.
2. Dựng `SoftwareStoryboardRenderer(doc, <thư mục chứa .osb>, W, H)`, với W×H = kích thước nguồn.
   Mapping widescreen của renderer trùng với mapping auto-cover tại tâm (320, 240) cho mọi tỉ lệ khung,
   nên render là 1:1 và sampling nearest là copy chính xác. **Kiểm chứng điều này trước:** ở frame 0,
   các tile không vi phạm phải có PSNR ≥ 45 dB.
3. Decode nguồn bằng `FrameSource` (W×H và fps của clip, toàn bộ clip). Với frame i, render tại giữa
   frame `t_i = (i + 0.5)·1000/fps`.
4. Mỗi frame, tính PSNR của cả frame và TileSsim theo Phụ lục A.1. Lưu mảng giá trị theo frame.
5. **Phân loại** mỗi tile 64×64 vi phạm sàn Medium (block PSNR < 34 trên tile đó, hoặc tile SSIM < 0,88):
   - tìm object trên cùng đang sống tại `t_i` phủ tâm tile. Không có thì loại là `uncovered`;
   - tính thời điểm chụp `t_c`: với sprite là `StartMs` của object; với animation là
     `StartMs + k·FrameDelay`, trong đó k là frame animation đang hiển thị tại `t_i`. Frame nguồn chụp là
     `c = round(t_c·fps/1000)`, clamp vào cửa sổ;
   - gọi `R_i` là vùng tile trên ảnh render, `S_c` và `S_i` là cùng vùng trên frame nguồn c và i.
     `q_enc = PSNR(R_i, S_c)` và `q_stale = PSNR(S_c, S_i)`;
   - `q_enc < 34`: loại `encode-loss` (asset đã sai ngay lúc chụp). Ngược lại, nếu `q_stale < 34`: loại
     `stale` (nguồn đã đổi mà tile vẫn giữ). Còn lại: `other` (vi phạm do SSIM, hoặc do cộng dồn).
6. Summary cho mỗi fixture:
   - min, p1, p5, mean của PSNR frame và của TileSsim.Min theo frame;
   - % frame vi phạm Low, Medium, High (frame vi phạm khi PSNR frame < P hoặc TileSsim.Min < S);
   - số tile-frame vi phạm Medium, tách theo 4 loại, kèm tỉ lệ %;
   - 3 frame tệ nhất: lưu `worst_<i>.png` gồm nguồn và render v1 đặt cạnh nhau, cộng crop phóng 4× quanh
     tile tệ nhất.
7. Lưu thêm render v1 tại các frame `floor(0,25N)`, `floor(0,5N)`, `floor(0,75N)` cho V4, dưới tên
   `OUT\v1\<fixture>\v1_f<i>.png`.

Output: `OUT\v1\<fixture>\summary.md` và `OUT\v1\summary.md` (bảng tổng, kèm kết luận G1).

### V2, V3a, V6, V9. Simulator hard-swap (một lần chạy cho mỗi fixture)

**Mục đích:** H2, H3, H6, H9.

Subcommand: `simulate <fixture>`. Chạy cho mọi fixture, trên đúng nguồn mà V0 đã dùng (easy là file
gốc; medium và hard là clip `standard`). Thuật toán ở Phụ lục A.7 (simulator S0).

Các tổ hợp chạy **lockstep trên cùng một lần decode**. Mỗi tổ hợp có trạng thái riêng; các tổ hợp của
một frame được xử lý bằng `Parallel.ForEach`.

- **Lưới V2:** PSNR {30, 32, 34, 36, 38, 40} × TileSsim {0,80; 0,85; 0,88; 0,92} = 24 tổ hợp, rect tự do.
- **V3a:** thêm 1 tổ hợp với sàn = (p1 PSNR, p1 TileSsim) của v1 trên chính fixture đó, lấy từ V1. Vì
  vậy **V1 phải xong trước**.
- **V6:** thêm 2 tổ hợp Medium và High với rect bắt theo lưới 128 px (Phụ lục A.3, chế độ `tileSnap`).
- **V9:** tổ hợp Medium rect tự do được đo thời gian từng khâu (Phụ lục A.8).
- **Export cho V4 và V8:**
  - lưu ảnh recon tại 3 frame `floor(0,25N)`, `floor(0,5N)`, `floor(0,75N)` cho 9 tổ hợp: hàng TileSsim
    0,88 × 6 mức PSNR, cộng cột PSNR 34 × 3 mức TileSsim còn lại;
  - riêng `hard/minecraft` và `medium/fish-short`, tổ hợp Medium rect tự do được export đầy đủ: danh
    sách item và PNG
    (Phụ lục A.7, "export").

Mỗi tổ hợp báo cáo:

- frame, sprite (số patch), asset khác nhau, bytes PNG, bytes `.osb` ước tính, tổng bytes;
- % diện tích refresh mỗi frame (mean, p95, max);
- alive (mean, peak);
- số frame bị xử lý như cut;
- tỉ lệ bytes so với v1-baseline.

Output: `OUT\v2\<fixture>\summary.md`, `OUT\v2\summary.md` (bảng tổng, kèm kết luận G2, G3a, G6) và ảnh
recon.

**Self-check bắt buộc** (chỉ tổ hợp Medium của `easy/sequence`): sau khi xử lý frame i, tính lại ErrorMap
của recon so với frame i. Không được còn block `Violating`. Vi phạm thì dừng và báo bug.

### V3b. Phương án đơn giản: v1-strict

**Mục đích:** G3b. Nếu chỉ cần chỉnh tham số v1 cho chặt là đủ, thì closed loop là thừa.

Chạy lệnh `bench` ẩn của v1, bằng exe CLI trong worktree, với từng fixture, trên đúng nguồn mà V0 đã
dùng. W×H là kích thước của nguồn đó (clip hoặc file easy):

```text
osbmpeg.exe bench <nguồn> -s <W>x<H> --keep-source --tile-size 64 --hash-quant 256 --colors 0 --tile-tolerance 0 --keep-artifacts -o OUT\v3\<fixture> --stats-json OUT\v3\<fixture>\stats.json --no-progress --ffmpeg-path <thư mục ffmpeg>
```

`--hash-quant 256` tắt lượng tử hóa hash (`ContentHasher.Quantize` copy nguyên byte), nên mọi thay đổi
pixel đều đóng run: v1 trở thành phát hiện thay đổi chính xác. Ghi bytes (`sb\` cộng `out.osb`), số
sprite và wall time, rồi chạy `diagnose-v1` trên `OUT\v3\<fixture>\out.osb` để lấy chất lượng.

Kết luận G3b: so bytes của simulator ở High (38/0,92, rect tự do) với bytes v1-strict. Ghi thêm v1-strict
có đạt High hay không. Nếu chưa đạt thì ghi rõ: khi đó phép so càng có lợi cho v1-strict, vì nó rẻ hơn
một phương án đạt High thật.

Output: `OUT\v3\summary.md`.

### V5. Giá trị của Photometric, Crossfade và lookahead

**Mục đích:** H5, G5a, G5b, G5c. Quyết định thành phần của M1.

Subcommand: `simulate-x <fixture>`. Thuật toán ở Phụ lục A.9 (SimulatorX, có display model).

- **Độ phân giải:** medium và hard dùng clip `standard` nhưng decode ở **chiều cao 360** (`FrameSource`
  scale, width giữ tỉ lệ và chẵn). Easy chạy ở độ phân giải gốc nếu cao ≤ 720; ngược lại cũng decode ở
  chiều cao 360. Synthetic chạy ở độ phân giải gốc. Mọi biến thể so với nhau ở cùng độ phân giải, nên tỉ
  lệ vẫn hợp lệ.
- **Sàn:** Medium.
- **Biến thể:**
  - `S0`: hard-swap nhân quả (như simulator V2, nhưng chạy trên SimulatorX để cùng code base);
  - `P`: S0 + Photometric nhân quả (chỉ lệnh `C`);
  - `C`: S0 + Crossfade nhân quả (B = frame hiện tại, span lùi về quá khứ);
  - `L`: S0 + patch lookahead (B chọn trong tương lai, K frame);
  - `CL`: Crossfade lookahead, rơi xuống patch lookahead khi không được;
  - `ALL`: P + CL + L.
- K = số frame của 1 giây (lookahead). Cửa sổ quá khứ của crossfade = 2 giây.
- **Fixture:** dissolve, fade_black, tint, static (synthetic), và mọi fixture thật.
- **Mỗi biến thể báo cáo:** asset khác nhau, bytes, sprite, số đoạn command (C hoặc F), peak alive, số
  lần provider được chọn, số frame được evaluate cho mỗi quyết định (mean, dùng cho V9). Riêng
  `fade_black`: số asset mới trong `[1 s, 3 s)`.

Output: `OUT\v5\summary.md`, gồm bảng biến thể × fixture (tỉ lệ so với S0) và kết luận G5a, G5b, G5c.
Summary cũng phải có mục "Đề xuất thành phần M1", áp quy tắc sau:

- một provider vào M1 khi nó giảm ≥ 50% trên fixture synthetic đích của nó, **hoặc** giảm ≥ 5% bytes trên
  ít nhất một fixture thật;
- lookahead vào M1 khi CL hoặc L thắng biến thể nhân quả tương ứng ≥ 10% bytes trên ít nhất 2 fixture
  thật, **hoặc** khi crossfade chỉ có giá trị lúc có lookahead (G5b xác nhận và G5c pass).

### V9. Hiệu năng

**Mục đích:** H9, G9. Không cần chạy riêng: dùng số đo thời gian từ V2 (tổ hợp Medium) và từ V5 (số frame
evaluate mỗi quyết định), cộng một micro-benchmark render.

Subcommand `perf-render <fixture>`:

1. Lấy danh sách item đã export của `hard/minecraft` ở Medium (V2), dựng `SbDocument` IR (sprite TopLeft cộng
   lệnh `S`, như Phụ lục A.7), rồi render 300 frame bằng `SoftwareStoryboardRenderer`. Đo ms mỗi frame.
2. Micro-benchmark "copy bilinear": một hàm bilinear đơn giản do spike tự viết, blit một ảnh 1920×1080 ở
   tỉ lệ 1:1, so với blit nearest. Lấy tỉ số làm hệ số bilinear.

Dự phóng (Phụ lục A.8):

```text
T_encode(frame) = decode + ErrorMap + DirtyRects + PNG + preview
    preview = (số frame evaluate mỗi quyết định từ V5) × (chi phí ErrorMap trên vùng dirty trung bình)
T_verify(frame) = decode + 2 × render (× hệ số bilinear) + PSNR + TileSsim
T(clip) = N_frame × (T_encode + T_verify)
T(minecraft full, native) = thời lượng gốc × fps gốc × (T_encode_native + T_verify_native), cộng một dòng tham khảo ở 60 fps để so với 542 phút
    với T_*_native = phần decode đo trực tiếp trên video gốc (bước 3)
                   + các khâu còn lại × (số pixel gốc / số pixel clip)
```

3. Đo tốc độ decode native: chỉ decode (không encode, không render) 10 giây gốc của `medium/fish` và
   `hard/minecraft` ở độ phân giải và fps gốc. Ghi ms mỗi frame.

Giả định "chi phí tỉ lệ tuyến tính theo số pixel" phải ghi trong báo cáo như một giới hạn. Kiểm tra nhanh
giả định này: chạy thêm Simulator ở tổ hợp Medium trên clip `quick` (cao 360) của cùng fixture. Nếu tỉ số
ms mỗi pixel giữa `quick` và `standard` lệch quá 30% thì ghi rõ trong báo cáo.

Output: `OUT\v9\summary.md` (bảng thời gian từng khâu, dự phóng, kết luận G9).

### U1. Gói review cho người dùng (V4, V7, V8): điểm dừng

Agent chuẩn bị **một** thư mục `OUT\review\`, gửi người dùng **một** lần, rồi dừng chờ.

**V4. Gallery chất lượng** (subcommand `gallery`). Tạo `OUT\review\v4\index.html`: file HTML tĩnh, ảnh
tham chiếu bằng đường dẫn tương đối, không cần server.

- Mỗi fixture, mỗi frame (25%, 50%, 75%) một hàng ảnh gồm: nguồn | v1 (từ V1) | recon tại các tổ hợp đã
  export (V2).
- Dưới mỗi ảnh ghi PSNR frame, TileSsim.Min, và bytes tổng của tổ hợp đó so với v1.
- Mỗi hàng có thêm một dải crop phóng 4× cùng vùng (vùng lấy theo tile tệ nhất của v1 tại frame đó).
- Ảnh có `image-rendering: pixelated`, và có nút bật/tắt để so hai ảnh chồng lên nhau.

**V7. Gói kiểm lazer** (subcommand `lazer-pack`). Tạo `OUT\review\v7\OsbMpegFeasibility-V7.osz` theo Phụ
lục B (beatmap) và Phụ lục C (các case). Kèm theo:

- `OUT\review\v7\expected\K<n>.png`: ảnh kỳ vọng, tính giải tích từ spec của case (không dùng renderer
  v1);
- `OUT\review\v7\probes.md`: bảng probe điểm và màu kỳ vọng.

**V8. Gói tải** (subcommand `workload-pack`). Tạo hai `.osz` từ clip `standard` của `hard/minecraft`
(10 giây, cao 720). Mỗi gói lặp nội dung đó 3 lần (offset +0 s, +10 s, +20 s, dùng chung asset) để có 30
giây phát:

- `OsbMpegFeasibility-V8-v1.osz`: object và asset từ output V0 của `hard/minecraft`;
- `OsbMpegFeasibility-V8-sim.osz`: item đã export của simulator (`hard/minecraft`, Medium), phát ra theo
  Phụ lục A.7 "export".

Clip 720p cho tải thấp hơn video 1080p gốc. Giới hạn này được ghi trong báo cáo. So sánh vẫn công bằng
vì hai gói dùng cùng một clip.

Cả hai dùng chung beatmap template ở Phụ lục B. Ghi kích thước từng gói.

**`OUT\review\README.md`** (người dùng đọc file này) gồm:

1. Hướng dẫn V4: mở `v4\index.html`. Mỗi fixture, trả lời: (a) v1 có mờ thấy rõ không; (b) mức sàn thấp
   nhất (PSNR/SSIM) mà bạn thấy không còn mờ so với nguồn; (c) có artifact nào khác không (khối, viền).
2. Hướng dẫn V7:
   - import `.osz` vào osu!lazer (kéo thả);
   - Settings: *Background dim 0%*, *Background blur 0%*, bật storyboard, tắt *Hit lighting*;
   - chọn Autoplay, nhấn Shift+Tab để ẩn HUD;
   - trong mỗi cửa sổ "hold" của từng case (bảng thời gian ở Phụ lục C), nhấn F12 để chụp màn hình;
   - chép ảnh vào `OUT\review\v7\screenshots\K<n>.png`;
   - xem K9 bằng mắt và ghi có nháy đen hay không;
   - chụp K6 và K7 ở độ phân giải bình thường của bạn; nếu được thì chụp thêm ở chế độ window kích thước
     lẻ (ví dụ 1500×900).
   - Ghi độ phân giải màn hình khi chụp.
3. Hướng dẫn V8: với mỗi gói, bật FPS counter (Ctrl+F11), phát 30 giây và ghi lại: FPS trung bình và
   thấp nhất, có giật không, thời gian từ lúc bấm play tới khi storyboard hiện (đo bằng đồng hồ), RAM
   đỉnh của osu! trong Task Manager.
4. Mẫu trả lời `OUT\review\answers.md` để người dùng điền (chép sẵn từng câu hỏi ở trên).

Sau khi người dùng gửi lại `answers.md` và ảnh chụp:

- **V7:** agent đổi tọa độ probe (storyboard space) sang pixel của ảnh chụp theo công thức ở Phụ lục C,
  lấy trung bình màu vùng 5×5 quanh mỗi probe và so với kỳ vọng. Với K6 so K7: căn hai ảnh theo cùng vùng
  canvas, tính hiệu tuyệt đối từng pixel, rồi trích các cột và hàng nằm dọc mép patch (tọa độ mép đã
  biết). Báo cáo max và mean của hiệu trên các đường đó, so với phần nội thất.
- **V4 và V8:** chép câu trả lời vào báo cáo, rồi kết luận G4 và G8.

### V11. Báo cáo và kết luận

Viết `docs/feasibility.md` trên `main` theo mẫu ở mục 6. Trình người dùng: kết luận go / go có điều
chỉnh / no-go, kèm danh sách thay đổi cho `implementation-plan.md`. **Không sửa** `implementation-plan.md`
cho tới khi người dùng đồng ý. Sau khi người dùng xem thì commit báo cáo (`docs: feasibility report`).
Worktree spike giữ lại cho tới khi người dùng bảo xóa.

---

## 5. Thứ tự, phụ thuộc và khối lượng

```text
F0 ──► V10b ──► make-clips standard, quick ──► V0 (chạy nền, dài) ──┐
  └──► V10a, V10c (song song với chuỗi trên) ───────────────────────┤
                                         ▼
                                  V1 (cần V0) ──► V2+V3a+V6+V9-đo (cần V1) ──► V9 dự phóng (cần V5)
                                  V3b (cần exe v1; diagnose cần V1 code)
                                  V5 (cần code simulator của V2)
                                         ▼
                           U1: V4 gallery + V7 pack + V8 packs ──► DỪNG chờ người dùng
                                         ▼
                                       V11 ──► DỪNG chờ người dùng
```

| Công việc | Khối lượng tương đối | Ghi chú |
| --- | --- | --- |
| F0 | nhỏ | |
| V0 | nhỏ (code), dài (chạy) | v1 tune cho từng fixture |
| V10a, b, c | nhỏ, nhỏ, vừa | |
| V1 | vừa | |
| Simulator (A.1–A.7) và V2 | lớn | phần code chính của spike |
| V3b | nhỏ | dùng `bench` có sẵn |
| SimulatorX và V5 | lớn | display model cùng 5 biến thể |
| V9 | nhỏ | |
| U1 | vừa | HTML, `.osz`, ảnh kỳ vọng |
| V11 | nhỏ | |

---

## 6. Mẫu báo cáo `docs/feasibility.md`

```markdown
# OsbMpeg: báo cáo kiểm chứng trước triển khai
Ngày, commit v1-baseline, commit spike, máy chạy (CPU, GPU, RAM, ổ đĩa), phiên bản ffmpeg, trạng thái Defender, độ phân giải màn hình lúc test lazer.

## 1. Kết luận
Go / Go có điều chỉnh / No-go, một đoạn lý do.

## 2. Kết quả theo tiêu chí
Bảng G1..G10: tiêu chí | ngưỡng | số đo | pass/fail | nguồn (OUT\...\summary.md).

## 3. Số liệu chính
3.1 Baseline v1 (V0). 3.2 Chẩn đoán v1 (V1). 3.3 Đường cong sàn–chi phí (V2), một bảng mỗi fixture.
3.4 Cùng chất lượng và v1-strict (V3). 3.5 Provider và lookahead (V5). 3.6 Disjoint (V6).
3.7 Hiệu năng (V9). 3.8 lazer (V7), kèm ảnh chụp đại diện. 3.9 Tải (V8). 3.10 Probe kỹ thuật (V10).

## 4. Nhận định của người dùng (V4, V7, V8)
Chép nguyên văn answers.md.

## 5. Thay đổi đề xuất cho implementation-plan.md
Danh sách đánh số, mỗi mục: phần nào của plan, thay đổi gì, bằng chứng nào.

## 6. Amendment
Tiêu chí nào đã sửa, lúc nào, vì sao (nếu có).

## 7. Giới hạn của kiểm chứng
Ví dụ: mọi số đo medium và hard là trên clip dẫn xuất (10 s, 720p, fps ≤ 30), không phải video gốc; simulator là cận trên; V5 chạy ở chiều cao 360; dự phóng native giả định chi phí tuyến tính theo pixel; hệ số bilinear chỉ là ước lượng; lazer test trên một máy.
```

---

## 7. Không làm

- Không sửa `src/` hay `tests/` trên `main`. Không refactor, không xóa code nào của v1.
- Không bắt đầu Phase A. Không sửa `implementation-plan.md` trước V11.
- Không tối ưu simulator vượt mức cần để chạy xong trong thời gian hợp lý. Được phép dùng
  `Parallel.ForEach` giữa các tổ hợp.
- Không merge hay push nhánh spike.
- Không đưa JPEG, motion hay animation vào spike: chúng nằm ngoài câu hỏi khả thi của M1.

---

## Phụ lục A: định nghĩa thuật toán dùng chung

### A.1 Metric

- `Luma(r, g, b) = (77r + 150g + 29b) >> 8`.
- `BlockMse(a, b, rect)` = trung bình `(Δ)²` trên cả 3 kênh RGB của mọi pixel trong rect.
  `BlockPsnr = mse == 0 ? 100 : 10·log10(255²/mse)`.
- `FramePsnr` = `BlockPsnr` trên toàn frame (khớp với `Metrics.Psnr`).
- `TileSsim`:
  - lưới tile 64×64 không chồng, bắt đầu từ (0, 0);
  - dải mép hẹp hơn 16 px gộp vào tile kề;
  - trên luma, mỗi tile tính mean, var và cov trực tiếp;
  - `ssim = ((2μaμb + C1)(2σab + C2)) / ((μa² + μb² + C1)(σa² + σb² + C2))`, với `C1 = (0.01·255)²` và
    `C2 = (0.03·255)²`;
  - trả `(Min, P1, Mean, MinTileX, MinTileY)`.
- **Self-test:**
  - `TileSsim(x, x).Min == 1`;
  - ảnh 512×512 có một tile bị invert: `Min < 0.2` và `Mean > 0.95`;
  - kích thước 1000×562 phủ mọi pixel đúng một lần;
  - `BlockPsnr` của hai ảnh giống nhau = 100.

### A.2 ErrorMap

- Block 16×16. Block mép co lại theo phần nằm trong frame.
- Phân loại block:
  - `Violating` khi `BlockPsnr < P`;
  - `NearFloor` khi `BlockPsnr < P + 2`;
  - `Clean` còn lại.
- Theo tile 64×64 (cùng lưới với A.1):
  - `ssim < S`: mọi block của tile thành `Violating`;
  - `ssim < S + 0.02`: block `Clean` của tile thành `NearFloor`.
- `ViolatingFraction` = số block `Violating` chia tổng số block.
- Simulator chỉ dùng `Violating`. `NearFloor` được tính nhưng không dùng (simulator là cận trên, không
  có hysteresis).
- **Self-test:** ảnh giống nhau thì toàn `Clean`; nhiễu mạnh trong đúng một block thì đúng block đó
  `Violating`; invert một tile thì 16 block `Violating`.

### A.3 DirtyRects

Input là bitmap block (true = cần xử lý). Output là danh sách rect pixel.

1. Tìm connected component 4-connectivity, lấy bbox theo block của mỗi component.
2. Nếu bbox có tỉ lệ lấp < 0,5 và diện tích > 64 block, cắt một lần theo hàng hoặc cột **toàn false**
   dài nhất bên trong bbox (nếu có), rồi tính lại bbox của hai phần.
3. Nếu một chiều của rect vượt 1024 px, chia rect đó thành các phần bằng nhau ≤ 1024 px, biên căn bội 16.
4. Rect nhỏ hơn 32×32 px gộp vào rect kề gần nhất nếu khoảng cách giữa hai bbox < 32 px (bbox hợp nhất,
   lặp tới khi hết thay đổi). Không gộp nếu rect hợp nhất vượt 1024 px ở một chiều.
5. Đổi sang pixel và cắt theo biên frame.

**Chế độ `tileSnap = 128`:** bỏ bước 1 đến 4. Mỗi tile 128×128 (theo lưới từ (0, 0), tile mép co lại)
chứa ít nhất một block true thành đúng một rect.

**Self-test:** hình L cho 1 hoặc 2 rect, không phủ vùng trống lớn; hai đảo cách nhau 200 px cho 2 rect;
full frame 1920×1080 cho 4 rect ≤ 1024; một block lẻ ở góc cho một rect 16×16 (hoặc bị gộp nếu gần).

### A.4 Ownership (dọn sprite bị che kín)

- `owner[block]` = id của item trên cùng phủ trọn phần trong frame của block, hoặc -1. Mỗi item có
  `ownedCount`.
- Commit patch P trên rect R: với mỗi block mà R phủ trọn, giảm `ownedCount` của chủ cũ (nếu có), rồi gán
  `owner = P` và tăng `ownedCount` của P. Chủ cũ nào về 0 thì đóng tại frame commit (`endFrame = i`).
- Alive tại frame i = số item có `ownedCount > 0` (đếm sau khi xử lý frame).
- **Self-test:**
  - patch phủ kín item cũ thì item cũ đóng;
  - phủ một nửa thì item cũ vẫn sống;
  - phủ nửa còn lại ở frame sau thì item cũ đóng tại frame đó.

### A.5 Chi phí PNG và dedupe

- Key = XXH3-128 trên byte `"OSBMPEG-ASSET-V1"` ‖ 0 ‖ 0 ‖ W (int32 LE) ‖ H ‖ 1 ‖ pixel RGB24 của crop.
- Key đã gặp trong tổ hợp này thì chi phí 0 (dedupe). Chưa gặp thì encode bằng ImageSharp:
  `Image.LoadPixelData<Rgb24>`, `SaveAsPng` với `PngEncoder { CompressionLevel = PngCompressionLevel.Level6 }`
  vào `MemoryStream`. Cộng `Length`, lưu key.
- Không ghi đĩa, trừ chế độ export.

### A.6 Bytes `.osb` ước tính

Mỗi sprite gồm 2 dòng: dòng header
`Sprite,Background,TopLeft,"sb/s/<32 hex>.png",<x>,<y>` và một dòng lệnh ` S,0,<start>,<end>,<scale>`.
Dựng đúng hai chuỗi này với giá trị thật (định dạng số như `OsbWriter`: thời gian là int, float dạng
`InvariantCulture`). Tính byte UTF-8 cộng 2 byte CRLF mỗi dòng. Với biến thể có track, cộng thêm mỗi
đoạn một dòng ` C,...` hoặc ` F,...` dựng tương tự.

### A.7 Simulator S0 (hard-swap nhân quả)

Trạng thái mỗi tổ hợp: `recon` (RGB24 W×H), `owner[]`, danh sách item
`(id, rect, startFrame, endFrame?, key)`, tập key đã gặp, các bộ đếm.

```text
với mỗi frame i (0..N-1), nguồn F_i:
    nếu i == 0: dirty = mọi block                                        # I-frame
    ngược lại:
        E = ErrorMap(recon, F_i, P, S)
        nếu E.ViolatingFraction ≥ 0.7: dirty = mọi block; cutFrames++    # cut
        ngược lại: dirty = block Violating
    nếu dirty rỗng: ghi số liệu frame, sang frame sau
    rects = DirtyRects(dirty)              (hoặc tileSnap)
    mỗi rect R:
        crop = F_i[R]; chi phí theo A.5; sprites++
        copy crop vào recon[R]; tạo item mới (startFrame = i); Ownership.Commit(item, R, i)
    ghi số liệu frame: diện tích refresh, alive
kết thúc: mọi item còn mở đóng tại N
```

**Export** (khi bật):

- ghi PNG của mỗi key mới vào `<pack>/sb/s/<key>.png`;
- ghi danh sách item ra `items.json` gồm rect, `startFrame`, `endFrame`, key;
- khi sinh `.osb` (V8, V9), mỗi item thành
  `Sprite,Background,TopLeft,"sb/s/<key>.png",x,y`, với `(x, y) = CanvasMapping(W, H, 320, 240).PixelToStoryboard(R.X, R.Y)`,
  cộng một lệnh `S,0,t0,t1,StoryboardScale`, trong đó `t0 = round(startFrame·1000/fps)` và
  `t1 = round(endFrame·1000/fps)` (làm tròn half-away-from-zero).

### A.8 Đo thời gian (V9)

Trong tổ hợp Medium rect tự do, dùng `Stopwatch` cộng dồn theo khâu:

- chờ decode (thời gian chờ frame từ `FrameSource`);
- ErrorMap;
- DirtyRects;
- crop và copy;
- hash;
- PNG encode (đếm thêm số MPixel đã encode).

Chạy riêng một lượt chỉ tổ hợp đó (không lockstep), để không bị nhiễu bởi `Parallel.ForEach`. Báo ms
trên mỗi frame và MPixel/giây của PNG.

### A.9 SimulatorX (display model cho V5)

**Item:**

- `rect`, `pixels` (RGB24);
- `order` (thứ tự tạo);
- `startFrame`, `endFrame?`;
- `alpha(f)`: tuyến tính từng đoạn theo frame, mặc định 1;
- `tint(f)`: RGB tuyến tính từng đoạn, mặc định (255, 255, 255).

**Vẽ recon tại frame f:**

- nền xám (128, 128, 128);
- vẽ theo `order` mọi item có `startFrame ≤ f < endFrame`: `px = pixels·tint(f)/255`, rồi
  `dst = dst·(1 − a) + px·a`, làm tròn về byte;
- mỗi frame chỉ vẽ lại **vùng hợp** của các item có track đang đổi hoặc có start/end rơi vào frame đó.
  Được phép vẽ lại toàn frame nếu đơn giản hơn, vì V5 chạy ở độ phân giải đã giảm.

**Đánh giá "pass tại frame f trên rect R":** mọi block trong R có `BlockPsnr ≥ P`, và mọi tile giao R có
`ssim ≥ S`. Hai giá trị tính trên ảnh ứng viên so với nguồn f.

**Luồng:** giống S0, nhưng tại mỗi rect dirty thử provider theo thứ tự (chỉ những provider mà biến thể
bật), nhận ứng viên đầu tiên pass, còn không thì patch (S0 hoặc L).

- **P (Photometric, chỉ `C`):**
  - Điều kiện: tìm item X là chủ của ≥ 90% block trong R; R phủ ≥ 90% `X.rect`; X có alpha 1; không có
    item nào khác X có track đang đổi giao R trong `[i − 1, i]`.
  - Fit mỗi kênh `c = 255·Σ(A·T)/Σ(A²)`, với A = `X.pixels`, T = nguồn `F_i[X.rect]`. Kênh nào có c > 255
    thì bỏ.
  - Ứng viên: đoạn tint của X đi từ `tint(i − 1)` tới c, trên `[i − 1, i]`.
  - Pass tại i thì commit: thêm đoạn vào track, **không tạo asset**, đếm thêm 1 đoạn command.
- **C (Crossfade nhân quả):**
  - Điều kiện: R đã có chủ; R không bị vi phạm liên tục trong 4 frame gần nhất; không có item có track
    đang đổi giao R trong cửa sổ quá khứ.
  - A = recon hiện tại trên R, B = `F_i[R]`.
  - Thử `t_s ∈ {i − w, i − w/2, i − w/4, …, i − 2}` với w = min(độ dài cửa sổ quá khứ,
    `i − startFrame` nhỏ nhất của các chủ hiện tại).
  - Pass khi mọi f ∈ `[t_s, i]` pass với `lerp(A, B, (f − t_s)/(i − t_s))` so với nguồn f.
  - Commit: item B có alpha tăng từ 0 lên 1 trên `[t_s, i]`, tốn 1 asset; chủ cũ đóng tại i qua
    Ownership.
- **L (patch lookahead):**
  - Với m = K, K − 2, …, 0 (bước 2): `B_m = F_{i+m}[R]`. Hợp lệ khi mọi f ∈ `[i, i + m]` pass với `B_m`
    so với nguồn f.
  - Chọn m lớn nhất hợp lệ. m = 0 luôn hợp lệ (chính là S0).
  - Commit B_m làm patch bắt đầu tại i.
  - Cần giữ trước K frame tương lai: decode trước vào ring buffer.
- **CL (Crossfade lookahead):**
  - Điều kiện như C.
  - Duyệt j từ `i + K` xuống `i + 2` (bước 2) và `t_s ∈ {i − 1, i − w/4, i − w/2}`.
  - Pass khi mọi f ∈ `[t_s, j]` pass với `lerp(A, F_j[R], (f − t_s)/(j − t_s))`.
  - Chọn j lớn nhất, rồi t_s sớm nhất.
  - Commit: item B = `F_j[R]` có alpha tăng từ 0 lên 1 trên `[t_s, j]`; chủ cũ đóng tại j. Recon của các
    frame `(i, j]` trên R đã được xác định bởi item này.
  - Không pass thì rơi xuống L.
- **Đo:** như S0, cộng số đoạn command và số frame evaluate mỗi quyết định.
- **Self-test của SimulatorX:** với biến thể S0, trên fixture static và `easy/sequence` ở cùng độ phân giải, SimulatorX
  phải cho **cùng** số sprite và cùng bytes như Simulator S0 (bằng chứng hai code base nhất quán).

---

## Phụ lục B: beatmap template cho các gói `.osz`

Thư mục gói gồm các file sau (tên file giữ nguyên văn):

- `audio.ogg`: im lặng, dài bằng tổng thời lượng test cộng 2 giây. Tạo bằng
  `ffmpeg -y -f lavfi -i anullsrc=r=44100:cl=stereo -t <giây> -c:a libvorbis audio.ogg`.
- `OsbMpeg - Feasibility <Tên> (spike) [Test].osu`:

```text
osu file format v14

[General]
AudioFilename: audio.ogg
AudioLeadIn: 0
PreviewTime: -1
Countdown: 0
SampleSet: Normal
StackLeniency: 0.7
Mode: 0
LetterboxInBreaks: 0
WidescreenStoryboard: 1

[Metadata]
Title:Feasibility <Tên>
Artist:OsbMpeg
Creator:spike
Version:Test
Source:
Tags:
BeatmapID:0
BeatmapSetID:-1

[Difficulty]
HPDrainRate:0
CircleSize:4
OverallDifficulty:0
ApproachRate:5
SliderMultiplier:1.4
SliderTickRate:1

[Events]
//Background and Video events
//Break Periods
//Storyboard Layer 0 (Background)
//Storyboard Layer 1 (Fail)
//Storyboard Layer 2 (Pass)
//Storyboard Layer 3 (Foreground)
//Storyboard Layer 4 (Overlay)
//Storyboard Sound Samples

[TimingPoints]
0,500,4,1,0,100,1,0

[HitObjects]
0,0,500,1,0,0:0:0:0:
0,0,<tổng thời lượng test ms>,1,0,0:0:0:0:
```

Hai hit object nằm ở góc trái trên của playfield, ở đầu và cuối, để cursor của Autoplay đứng yên ở góc,
xa các probe.

- `OsbMpeg - Feasibility <Tên> (spike).osb`: storyboard. Ghi bằng `OsbWriter` của v1 (qua `SbDocument`),
  hoặc viết tay theo đúng định dạng đó.
- `sb/…`: asset PNG.
- Nén thư mục thành `.osz` (định dạng zip, đuôi `.osz`) bằng `System.IO.Compression.ZipFile`.

---

## Phụ lục C: các case kiểm lazer (V7)

- **Thời gian:** case Kn chiếm `[T0, T0 + 4000)` với `T0 = 2000 + 5000·n` (ms). Cửa sổ "hold" để chụp là
  `[T0 + 1500, T0 + 3500)`. Tổng thời lượng test = `2000 + 5000·(số case)`.
- **Tọa độ:** storyboard space 640×480, widescreen kéo từ x = −107 tới 747.
- **Đổi probe (x, y) sang pixel ảnh chụp kích thước Sw×Sh (vùng game full, không viền):**
  `k = Sh/480`, `px = (x + (Sw/k − 640)/2)·k`, `py = y·k`.
- **Asset** (spike tự tạo):
  - `sb/w.png`: trắng 64×64;
  - `sb/r.png`: đỏ (255, 0, 0) 64×64;
  - `sb/b.png`: xanh (0, 0, 255) 64×64;
  - frame thật cho K6 đến K8 là một frame của `medium/fish` ở **độ phân giải gốc 1920×1080** (không
    dùng clip, vì phép thử đường nối cần đúng tỉ lệ scale thật), lấy tại thời điểm `From` của cửa sổ
    `standard`: `ffmpeg -ss <From> -i "tests/fixtures/medium/Fish spinning.mp4" -frames:v 1 -pix_fmt rgb24 fish.png`.
    Mọi chỗ ghi "frame fish" trong bảng dưới đây đều là ảnh này.
- Mọi sprite dùng layer Background, trừ khi ghi khác. Ngoài cửa sổ của mỗi case, không sprite nào của case
  đó còn sống.

| Case | Nội dung (lệnh) | Kỳ vọng trong cửa sổ hold | Probe (x, y) → RGB |
| --- | --- | --- | --- |
| K0 gap-hold | `w.png` Centre (320,240); `S,0,T0,T0+4000,3`; `F,0,T0,T0+100,0,1`; `F,0,T0+3900,T0+4000,1,0` | Hình vuông trắng **hiện**, vì lazer giữ End của F trước (bug của evaluator v1 sẽ cho ẩn) | (320,240) → 255,255,255 |
| K1 S×V | `w.png` Centre (320,240); `S,0,T0,T0+4000,2`; `V,0,T0,T0+4000,1,0.5` | Hình chữ nhật 128×64 đơn vị (S nhân V) | (380,240) trắng; (320,268) trắng; (320,280) đen; (395,240) đen |
| K2 crossfade 50% | `r.png` Centre (320,240) `S,0,T0,T0+4000,3`; rồi `b.png` Centre (320,240) `S,0,T0,T0+4000,3`, `F,0,T0,T0+1000,0,0.5`, `F,0,T0+1000,T0+4000,0.5` | Màu tím lerp straight alpha | (320,240) → 128,0,128 |
| K3 tint | `w.png` Centre (320,240) `S,0,T0,T0+4000,3`; `C,0,T0,T0+4000,128,64,255` | Màu tint | (320,240) → 128,64,255 |
| K4 phơi sáng hai lần | Hai `w.png` Centre tại (290,240) và (350,240), mỗi cái `S,0,T0,T0+4000,2` và `F,0,T0,T0+4000,0.5` | Vùng giao sáng hơn vùng đơn | (320,240) → 191; (270,240) → 128 |
| K5 additive | Như K4 nhưng mỗi sprite thêm `C,0,T0,T0+4000,100,100,100`, `F` = 1, và `P,0,T0,T0+4000,A` | Cộng dồn | (320,240) → 200; (270,240) → 100 |
| K6 mosaic | Frame fish cắt thành các rect theo lưới không đều (cột 0–256–768–1024–1920, hàng 0–144–544–1080 px; thêm một rect 48×48 ở (1000,500) đè lên). Mỗi rect một sprite TopLeft tại `CanvasMapping(1920,1080,320,240).PixelToStoryboard`, cộng `S,0,T0,T0+4000,0.444444`. Sprite 48×48 khai báo sau cùng. | Giống hệt K7, **không có đường nối** | So số với K7 (G7) |
| K7 tham chiếu | Cùng frame fish, một sprite duy nhất TopLeft, cùng mapping | Ảnh gốc | Nguồn so sánh cho K6 |
| K8 mosaic xoay | Như K6, mỗi tile xoay 0,3 rad quanh tâm (320,240): tâm tile đổi theo công thức của `GroupTransformBaker`; mỗi tile Centre, cộng `R,0,T0,T0+4000,0.3` | Ảnh xoay liền mạch | Người dùng quan sát đường nối |
| K9 chuỗi 60fps | 180 sprite `w.png` Centre (320,240) `S` phủ toàn màn hình (hệ số 15), sprite thứ k sống `[round(T0 + k·1000/60), round(T0 + (k+1)·1000/60))`, `C` đổi hue theo k (vòng màu 3 giây) | Màu đổi mượt, **không có frame đen** | Quan sát bằng mắt (G7) |

Ảnh kỳ vọng `expected/K<n>.png` (1920×1080) được sinh giải tích từ bảng trên: hình chữ nhật đặc, màu
tính tay. Với K6 và K7 thì ảnh kỳ vọng là frame fish đặt theo mapping. Không dùng renderer v1 cho ảnh kỳ
vọng.
