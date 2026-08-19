using System.ComponentModel;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli.Settings;

public sealed class InspectSettings : CommandSettings
{
    [CommandArgument(0, "<input.osb>")] public string Input { get; set; } = "";

    [CommandOption("--format <FORMAT>")]
    [Description("text|json. Default: text.")]
    public string Format { get; set; } = "text";
}