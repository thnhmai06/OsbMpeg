using System.ComponentModel;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli.Settings;

public sealed class CompileSettings : CommandSettings
{
    [CommandArgument(0, "<input.osbv>")] public string Input { get; set; } = "";

    [CommandArgument(1, "<output.osb>")] public string Output { get; set; } = "";

    [CommandArgument(2, "<assets-dir>")] public string AssetDir { get; set; } = "";

    [CommandOption("--hwaccel <MODE>")]
    [Description(
        "Hardware acceleration mode forwarded to ffmpeg as \"-hwaccel MODE\" (e.g. cuda, qsv, vaapi). Omit for CPU decode.")]
    public string? HwAccel { get; set; }
}