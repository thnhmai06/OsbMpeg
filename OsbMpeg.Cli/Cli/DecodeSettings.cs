using Spectre.Console.Cli;
using System.ComponentModel;

namespace OsbMpeg.Cli;

public sealed class DecodeSettings : CommandSettings
{
    [CommandArgument(0, "<input.osb>")]
    public string Input { get; set; } = "";

    [CommandArgument(1, "<output>")]
    public string Output { get; set; } = "";

    [CommandOption("-y|--overwrite")]
    [Description("Overwrite output without asking.")]
    public bool Overwrite { get; set; }

    [CommandOption("-s|--size <WxH>")]
    [Description("Render resolution. Default: 1920x1080.")]
    public string Size { get; set; } = "1920x1080";

    [CommandOption("-r|--fps <FPS>")]
    [Description("Output frame rate. Default: 30.")]
    public double Fps { get; set; } = 30;

    [CommandOption("--ffmpeg-path <DIR>")]
    public string? FFmpegPath { get; set; }

    [CommandOption("--no-progress")]
    public bool NoProgress { get; set; }
}
