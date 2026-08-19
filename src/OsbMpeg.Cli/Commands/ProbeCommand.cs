using FFMpegCore;
using OsbMpeg.Cli.Settings;
using OsbMpeg.Compiler.Media;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli.Commands;

public sealed class ProbeCommand : AsyncCommand<ProbeSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProbeSettings settings,
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

        var table = new Table().Border(TableBorder.Rounded).Title(settings.Input);
        table.AddColumn("");
        table.AddColumn("");
        table.HideHeaders();
        table.AddRow("Resolution", $"{info.Width}x{info.Height}");
        table.AddRow("FPS (avg)", info.SourceFps.ToString("0.###"));
        table.AddRow("Duration", info.Duration.ToString(@"hh\:mm\:ss\.ff"));
        table.AddRow("Codec", info.CodecName);
        AnsiConsole.Write(table);

        return 0;
    }
}