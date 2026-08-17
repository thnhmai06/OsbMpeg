using System.Diagnostics;
using FFMpegCore;
using OsbMpeg.Coding;
using OsbMpeg.Encoder;
using OsbMpeg.Media;
using OsbMpeg.Osb;
using OsbMpeg.Render;
using OsbMpeg.Ui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli;

public sealed class BenchCommand : AsyncCommand<BenchSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, BenchSettings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.Input))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] input file not found: {settings.Input}");
            return 1;
        }

        if (settings.FFmpegPath is not null)
            GlobalFFOptions.Configure(o => o.BinaryFolder = settings.FFmpegPath);

        var workDir = settings.OutDir is not null
            ? Path.GetFullPath(settings.OutDir)
            : Path.Combine(Path.GetTempPath(), "osbmpeg_bench_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var (width, height) = EncodeOptions.ParseSize(settings.Size);
        var osbPath = Path.Combine(workDir, "out.osb");
        var reconPath = Path.Combine(workDir, "recon.mp4");

        var options = new EncodeOptions(
            InputPath: Path.GetFullPath(settings.Input),
            OutputPath: osbPath,
            AssetDir: Path.Combine(workDir, "sb"),
            AssetRelativeDir: "sb",
            AssetNamePrefix: "sb",
            CanvasWidth: width,
            CanvasHeight: height,
            Fps: settings.Fps,
            KeepSource: settings.KeepSource,
            TileSize: settings.TileSize,
            HashQuantLevels: 32,
            Colors: 0,
            PngCompressionLevel: 6,
            FFmpegPath: settings.FFmpegPath,
            Start: EncodeOptions.ParseFFmpegTime(settings.Start),
            Duration: EncodeOptions.ParseFFmpegTime(settings.Duration));

        var encodeStats = await EncodeLiveView.RunAsync(new EncodePipeline(options), !settings.NoProgress);
        ReportTables.PrintEncodeSummary(encodeStats);

        var doc = OsbReader.Read(osbPath);
        var renderer = new SoftwareStoryboardRenderer(doc, workDir, encodeStats.Width, encodeStats.Height);
        var reconFrameCount = Math.Max(1, (int)Math.Ceiling(renderer.DurationMs / 1000.0 * encodeStats.Fps));

        var decodeSw = Stopwatch.StartNew();

        IEnumerable<FFMpegCore.Pipes.IVideoFrame> Frames()
        {
            for (var i = 0; i < reconFrameCount; i++)
            {
                var t = i * 1000.0 / encodeStats.Fps;
                yield return new CanvasVideoFrame(renderer.RenderFrame(t));
            }
        }

        if (settings.NoProgress)
            await FrameWriter.WriteAsync(Frames(), reconPath, encodeStats.Fps);
        else
            await AnsiConsole.Status().StartAsync($"Reconstructing {reconFrameCount} frames...", async _ =>
                await FrameWriter.WriteAsync(Frames(), reconPath, encodeStats.Fps));

        decodeSw.Stop();

        var (psnr, ssim, compared) = await ComparePsnrSsimAsync(options.InputPath, reconPath, encodeStats.Width, encodeStats.Height, encodeStats.Fps, options.Start, options.Duration);
        ReportTables.PrintQualityTable(psnr, ssim, decodeSw.Elapsed);

        if (settings.StatsJson is not null)
        {
            var combined = new
            {
                Encode = encodeStats,
                Quality = new { Psnr = psnr, Ssim = ssim, FramesCompared = compared },
                DecodeTime = decodeSw.Elapsed,
            };
            await File.WriteAllTextAsync(settings.StatsJson, System.Text.Json.JsonSerializer.Serialize(combined, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        if (!settings.KeepArtifacts && settings.OutDir is null)
            Directory.Delete(workDir, recursive: true);

        return 0;
    }

    private static async Task<(double Psnr, double Ssim, int Compared)> ComparePsnrSsimAsync(string originalPath, string reconPath, int width, int height, double fps, TimeSpan? start, TimeSpan? duration)
    {
        var origFrames = FrameSource.ReadFramesAsync(originalPath, new FrameSourceOptions(width, height, fps, start, duration));
        var reconFrames = FrameSource.ReadFramesAsync(reconPath, new FrameSourceOptions(width, height, fps));

        double psnrSum = 0, ssimSum = 0;
        var compared = 0;

        await using var e1 = origFrames.GetAsyncEnumerator();
        await using var e2 = reconFrames.GetAsyncEnumerator();

        while (await e1.MoveNextAsync() && await e2.MoveNextAsync())
        {
            using var f1 = e1.Current;
            using var f2 = e2.Current;
            psnrSum += Metrics.Psnr(f1.Rgb, f2.Rgb);
            ssimSum += Metrics.Ssim(Metrics.ToLuma(f1.Rgb), Metrics.ToLuma(f2.Rgb));
            compared++;
        }

        return compared == 0 ? (0, 0, 0) : (psnrSum / compared, ssimSum / compared, compared);
    }
}
