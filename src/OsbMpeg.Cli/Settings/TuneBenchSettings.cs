using System.ComponentModel;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli.Settings;

public sealed class TuneBenchSettings : CommandSettings
{
    [CommandArgument(0, "<input>")] public string Input { get; set; } = "";

    [CommandOption("-r|--fps <FPS>")]
    [Description("Target fps for the sample windows. Default: the source's own fps.")]
    public double? Fps { get; set; }

    [CommandOption("--ss|--start <TIME>")]
    [Description("Scene start (absolute input time, ffmpeg syntax). Default: 0 (whole file).")]
    public string? Start { get; set; }

    [CommandOption("-t|--duration <TIME>")]
    [Description("Scene duration. Default: to the end of the file.")]
    public string? Duration { get; set; }

    [CommandOption("--no-shared")]
    [Description("A/B: disable the shared sample-window decode — each probe decodes its own window via ffmpeg (the pre-optimization path).")]
    public bool NoShared { get; set; }

    [CommandOption("--ffmpeg-path <DIR>")]
    public string? FFmpegPath { get; set; }

    [CommandOption("--hwaccel <MODE>")]
    [Description("Hardware acceleration mode forwarded to ffmpeg as \"-hwaccel MODE\" (e.g. cuda, qsv, vaapi). Omit for CPU decode.")]
    public string? HwAccel { get; set; }

    [CommandOption("--no-progress")]
    public bool NoProgress { get; set; }
}