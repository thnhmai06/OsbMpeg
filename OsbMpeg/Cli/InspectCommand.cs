using OsbMpeg.Osb;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli;

public sealed class InspectCommand : AsyncCommand<InspectSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, InspectSettings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.Input))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] input file not found: {settings.Input}");
            return Task.FromResult(1);
        }

        var doc = OsbReader.Read(settings.Input);

        if (settings.Format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var summary = new
            {
                doc.SpriteCount,
                doc.AnimationCount,
                doc.CommandCount,
                Layers = doc.Layers.ToDictionary(l => l.Key.ToString(), l => l.Value.Count),
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return Task.FromResult(0);
        }

        var table = new Table().Border(TableBorder.Rounded).Title(settings.Input);
        table.AddColumn("");
        table.AddColumn("");
        table.HideHeaders();
        table.AddRow("Sprites", doc.SpriteCount.ToString("N0"));
        table.AddRow("Animations", doc.AnimationCount.ToString("N0"));
        table.AddRow("Commands", doc.CommandCount.ToString("N0"));
        foreach (var (layer, objects) in doc.Layers)
            table.AddRow($"  {layer}", objects.Count.ToString("N0"));
        AnsiConsole.Write(table);

        return Task.FromResult(0);
    }
}
