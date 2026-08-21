using System.Diagnostics;
using FFMpegCore;
using OsbMpeg.Cli.Settings;
using OsbMpeg.Compiler.Encode;
using OsbMpeg.Compiler.Shared.Media;
using OsbMpeg.Compiler.Tuning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli.Commands;

/// <summary>
///     Benchmark of the auto-tuner itself: runs ParameterTuner.TuneAsync against a scene segment
///     while collecting per-window stage timings (via onWindowTimed), then prints the stage-sum
///     table used to keep docs/research.md's tuning-cost entries honest. Not a product command —
///     a measurement instrument. The tuner's probes read from the shared sample-window decode
///     (Spec: share sample decodes across candidates), so the ffmpeg-spawn column here is the
///     pre-pass window count, not a per-probe process count.
/// </summary>
public sealed class TuneBenchCommand : AsyncCommand<TuneBenchSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, TuneBenchSettings settings,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.Input))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] input file not found: {settings.Input}");
            return 1;
        }

        if (settings.FFmpegPath is not null)
            GlobalFFOptions.Configure(o => o.BinaryFolder = settings.FFmpegPath);

        var info = await MediaProbe.AnalyseAsync(settings.Input, cancellationToken);
        var fps = settings.Fps ?? info.SourceFps;

        var startMs = ParseMs(settings.Start);
        var endMs = settings.Duration is not null
            ? startMs + ParseMs(settings.Duration)
            : info.Duration.TotalMilliseconds;

        var probeTimes = new List<ProbeWindowTimes>();

        var wall = Stopwatch.StartNew();
        var tuned = await ParameterTuner.TuneAsync(settings.Input, info, fps, startMs, endMs,
            startMs <= 0, settings.HwAccel,
            log: line => AnsiConsole.MarkupLineInterpolated($"[grey]{line.EscapeMarkup()}[/]"),
            cancellationToken, probeTimes.Add, shareDecode: !settings.NoShared, maxConcurrency: settings.MaxConcurrency);
        wall.Stop();

        AnsiConsole.MarkupLineInterpolated($"Tuned: [bold]{tuned.TileSize}/{tuned.HashQuantLevels}/{tuned.TileTolerance}/{tuned.Colors}[/] (tile/hashquant/tolerance/colors) in [bold]{wall.ElapsedMilliseconds}ms[/] wall");

        if (probeTimes.Count > 0)
            PrintStageTable(probeTimes);

        var peak = Process.GetCurrentProcess().PeakWorkingSet64;
        var windowsDecoded = probeTimes.Select(p => (p.WindowStartMs, p.WindowDurationMs)).Distinct().Count();
        var decodeMode = settings.NoShared ? " -- per-probe decode" : ", shared decode";
        AnsiConsole.MarkupLineInterpolated(
            $"Peak working set: [bold]{peak / (1024.0 * 1024.0 * 1024.0):F2} GB[/]; {windowsDecoded} decoded window(s) ({probeTimes.Count} probe windows total{decodeMode})");
        return 0;

        static double ParseMs(string? value) => EncodeOptions.ParseFFmpegTime(value)?.TotalMilliseconds ?? 0;
    }

    private static void PrintStageTable(IReadOnlyList<ProbeWindowTimes> probes)
    {
        var totals = new Dictionary<string, long>
        {
            ["frame wait (ffmpeg)"] = probes.Sum(p => p.FrameWaitMs),
            ["track runs"] = probes.Sum(p => p.TrackMs),
            ["quadtree merge"] = probes.Sum(p => p.MergeMs),
            ["animation detect"] = probes.Sum(p => p.DetectMs),
            ["emit (PNG encode)"] = probes.Sum(p => p.EmitMs),
            ["render recon"] = probes.Sum(p => p.RenderMs),
            ["psnr"] = probes.Sum(p => p.PsnrMs),
        };

        var totalMs = totals.Values.Sum();
        var probeWall = probes.Max(p => p.ProbeMs); // per-probe wall overlaps; report the longest

        var table = new Table()
            .AddColumn("stage")
            .AddColumn(new TableColumn("sum (ms)").RightAligned())
            .AddColumn(new TableColumn("share").RightAligned());

        foreach (var (stage, sumMs) in totals)
            table.AddRow(stage, sumMs.ToString("N0"), $"{(double)sumMs / totalMs:P1}");

        table.AddRow("[bold]encode total[/]", $"[bold]{totalMs:N0}[/]", "[bold]100%[/]");
        table.AddRow("probe wall (max single probe)", probeWall.ToString("N0"), "");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLineInterpolated(
            $"[grey]{probes.Count} probe windows across the shared decode pre-pass[/]");
    }
}