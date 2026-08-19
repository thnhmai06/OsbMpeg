using OsbMpeg.Cli.Settings;
using OsbMpeg.Compiler.Compilation;
using OsbMpeg.Parsers.Osbv;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli.Commands;

public sealed class CompileCommand : AsyncCommand<CompileSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CompileSettings settings,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.Input))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] input file not found: {settings.Input}");
            return 1;
        }

        OsbvDocument document;
        try
        {
            document = OsbvParser.ParseFile(settings.Input);
        }
        catch (OsbvParseException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]parse error:[/] {settings.Input}: {ex.Message}");
            return 1;
        }

        var result = await VideoCompiler.CompileAsync(document, settings.AssetDir, settings.Output, settings.HwAccel,
            msg => AnsiConsole.MarkupLineInterpolated($"[grey]{msg}[/]"), cancellationToken);

        AnsiConsole.MarkupLineInterpolated($"[green]compiled[/] {settings.Output}");
        AnsiConsole.MarkupLineInterpolated(
            $"  sprites={result.SpriteCount} animations={result.AnimationCount} commands={result.CommandCount} assets={result.AssetCount} video-sources={result.VideoSourceCount}");

        return 0;
    }
}