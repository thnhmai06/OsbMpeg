using Spectre.Console.Cli;
using System.ComponentModel;

namespace OsbMpeg.Cli;

public sealed class InspectSettings : CommandSettings
{
    [CommandArgument(0, "<input.osb>")]
    public string Input { get; set; } = "";

    [CommandOption("--format <FORMAT>")]
    [Description("text|json. Default: text.")]
    public string Format { get; set; } = "text";
}
