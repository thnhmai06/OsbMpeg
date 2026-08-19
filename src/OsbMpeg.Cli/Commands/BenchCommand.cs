using System.Diagnostics;
using System.Text.Json;
using FFMpegCore;
using FFMpegCore.Pipes;
using OsbMpeg.Cli.Formats;
using OsbMpeg.Cli.Settings;
using OsbMpeg.Compiler.Encoder;
using OsbMpeg.Compiler.Evaluation;
using OsbMpeg.Compiler.Media;
using OsbMpeg.Compiler.Render;
using OsbMpeg.Parsers.Osb;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli.Commands;

public sealed class BenchCommand : AsyncCommand<BenchSettings>
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    protected override async Task<int> ExecuteAsync(CommandContext context, BenchSettings settings,
        CancellationToken cancellationToken)
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
            Path.GetFullPath(settings.Input),
            osbPath,
            Path.Combine(workDir, "sb"),
            "sb",
            "sb",
            width,
            height,
            settings.Fps,
            settings.KeepSource,
            settings.TileSize,
            settings.HashQuantLevels,
            settings.Colors,
            6,
            RawSnapshot: settings.RawSnapshot,
            MinAnimationUniqueness: settings.MinAnimationUniqueness,
            TileTolerance: settings.TileTolerance,
            NoQuadtree: settings.NoQuadtree,
            Start: EncodeOptions.ParseFFmpegTime(settings.Start),
            Duration: EncodeOptions.ParseFFmpegTime(settings.Duration));

        var encodeStats =
            await EncodeLiveView.RunAsync(new EncodePipeline(options), !settings.NoProgress, cancellationToken);
        ReportTables.PrintEncodeSummary(encodeStats);

        var doc = OsbReader.Read(osbPath);
        var renderer = new SoftwareStoryboardRenderer(doc, workDir, encodeStats.Width, encodeStats.Height);
        var reconFrameCount = Math.Max(1, (int)Math.Ceiling(renderer.DurationMs / 1000.0 * encodeStats.Fps));

        var decodeSw = Stopwatch.StartNew();

        if (settings.NoProgress)
            await FrameWriter.WriteAsync(Frames(), reconPath, encodeStats.Fps, cancellationToken);
        else
            await AnsiConsole.Status().StartAsync($"Reconstructing {reconFrameCount} frames...", async _ =>
                await FrameWriter.WriteAsync(Frames(), reconPath, encodeStats.Fps, cancellationToken));

        decodeSw.Stop();

        var (psnr, ssim, compared) = await ComparePsnrSsimAsync(options.InputPath, reconPath, encodeStats.Width,
            encodeStats.Height, encodeStats.Fps, options.Start, options.Duration);
        ReportTables.PrintQualityTable(psnr, ssim, decodeSw.Elapsed);

        var peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
        AnsiConsole.MarkupLineInterpolated(
            $"Peak working set: [bold]{EncodeLiveView.FormatBytes(peakWorkingSetBytes)}[/]");

        if (settings.StatsJson is not null)
        {
            var combined = new
            {
                Encode = encodeStats,
                Quality = new { Psnr = psnr, Ssim = ssim, FramesCompared = compared },
                DecodeTime = decodeSw.Elapsed,
                PeakWorkingSetBytes = peakWorkingSetBytes
            };
            await File.WriteAllTextAsync(
                settings.StatsJson, JsonSerializer.Serialize(combined, SerializerOptions), cancellationToken);
        }

        if (settings is { KeepArtifacts: false, OutDir: null })
            Directory.Delete(workDir, true);

        return 0;

        IEnumerable<IVideoFrame> Frames()
        {
            for (var i = 0; i < reconFrameCount; i++)
            {
                var t = i * 1000.0 / encodeStats.Fps;
                yield return new CanvasVideoFrame(renderer.RenderFrame(t));
            }
        }
    }

    private static async Task<(double Psnr, double Ssim, int Compared)> ComparePsnrSsimAsync(string originalPath,
        string reconPath, int width, int height, double fps, TimeSpan? start, TimeSpan? duration)
    {
        var origFrames =
            FrameSource.ReadFramesAsync(originalPath, new FrameSourceOptions(width, height, fps, start, duration));
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