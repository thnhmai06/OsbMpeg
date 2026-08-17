using FFMpegCore;
using FFMpegCore.Pipes;
using OsbMpeg.Encoder;
using OsbMpeg.Media;
using OsbMpeg.Osb;
using OsbMpeg.Render;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli;

public sealed class DecodeCommand : AsyncCommand<DecodeSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DecodeSettings settings, CancellationToken cancellationToken)
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
            GlobalFFOptions.Configure(o => o.BinaryFolder = settings.FFmpegPath);

        var (width, height) = EncodeOptions.ParseSize(settings.Size);
        var doc = OsbReader.Read(settings.Input);
        var assetDir = Path.GetDirectoryName(Path.GetFullPath(settings.Input)) ?? ".";
        var renderer = new SoftwareStoryboardRenderer(doc, assetDir, width, height);
        var frameCount = Math.Max(1, (int)Math.Ceiling(renderer.DurationMs / 1000.0 * settings.Fps));

        IEnumerable<IVideoFrame> Frames()
        {
            for (var i = 0; i < frameCount; i++)
            {
                var t = i * 1000.0 / settings.Fps;
                yield return new Render.CanvasVideoFrame(renderer.RenderFrame(t));
            }
        }

        if (settings.NoProgress)
        {
            await FrameWriter.WriteAsync(Frames(), settings.Output, settings.Fps);
        }
        else
        {
            await AnsiConsole.Status().StartAsync($"Rendering {frameCount} frames...", async _ =>
                await FrameWriter.WriteAsync(Frames(), settings.Output, settings.Fps));
        }

        AnsiConsole.MarkupLineInterpolated($"[green]decoded[/] {frameCount} frames -> {settings.Output}");
        return 0;
    }
}
