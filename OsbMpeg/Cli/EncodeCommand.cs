using OsbMpeg.Encoder;
using OsbMpeg.Ui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli;

public sealed class EncodeCommand : AsyncCommand<EncodeSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EncodeSettings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.Input))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] input file not found: {settings.Input}");
            return 1;
        }

        if (File.Exists(settings.Output) && !settings.Overwrite)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] output exists: {settings.Output} (use -y to overwrite)");
            return 1;
        }

        if (settings.FFmpegPath is not null)
            FFMpegCore.GlobalFFOptions.Configure(o => o.BinaryFolder = settings.FFmpegPath);

        var (width, height) = EncodeOptions.ParseSize(settings.Size);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(settings.Output));
        var assetDir = settings.AssetDir is not null
            ? Path.GetFullPath(settings.AssetDir)
            : Path.Combine(string.IsNullOrEmpty(outputDir) ? "." : outputDir, "sb");

        var options = new EncodeOptions(
            InputPath: Path.GetFullPath(settings.Input),
            OutputPath: Path.GetFullPath(settings.Output),
            AssetDir: assetDir,
            AssetRelativeDir: "sb",
            AssetNamePrefix: settings.AssetNamePrefix,
            CanvasWidth: width,
            CanvasHeight: height,
            Fps: settings.Fps,
            KeepSource: settings.KeepSource,
            TileSize: settings.TileSize,
            HashQuantLevels: settings.HashQuantLevels,
            Colors: settings.Colors,
            PngCompressionLevel: settings.PngCompression,
            FFmpegPath: settings.FFmpegPath,
            Start: EncodeOptions.ParseFFmpegTime(settings.Start),
            Duration: EncodeOptions.ParseFFmpegTime(settings.Duration));

        var pipeline = new EncodePipeline(options);
        var stats = await EncodeLiveView.RunAsync(pipeline, !settings.NoProgress);

        ReportTables.PrintEncodeSummary(stats);

        if (settings.StatsJson is not null)
            await File.WriteAllTextAsync(settings.StatsJson, stats.ToJson());

        return 0;
    }
}
