using Spectre.Console.Cli;

namespace OsbMpeg.Cli.Settings;

public sealed class ProbeSettings : CommandSettings
{
    [CommandArgument(0, "<input>")] public string Input { get; set; } = "";

    [CommandOption("--ffmpeg-path <DIR>")] public string? FFmpegPath { get; set; }
}