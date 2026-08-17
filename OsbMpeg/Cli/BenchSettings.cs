using Spectre.Console.Cli;
using System.ComponentModel;

namespace OsbMpeg.Cli;

public sealed class BenchSettings : CommandSettings
{
    [CommandArgument(0, "<input>")]
    public string Input { get; set; } = "";

    [CommandOption("-s|--size <WxH>")]
    public string Size { get; set; } = "1920x1080";

    [CommandOption("--keep-source")]
    public bool KeepSource { get; set; }

    [CommandOption("-r|--fps <FPS>")]
    public double Fps { get; set; } = 30;

    [CommandOption("--ss|--start <TIME>")]
    public string? Start { get; set; }

    [CommandOption("-t|--duration <TIME>")]
    public string? Duration { get; set; }

    [CommandOption("--tile-size <PX>")]
    public int TileSize { get; set; } = 64;

    [CommandOption("--quality <SPEC>")]
    public string Quality { get; set; } = "psnr=35";

    [CommandOption("--keep-artifacts")]
    [Description("Keep the intermediate .osb, assets, and reconstructed video instead of using a temp dir.")]
    public bool KeepArtifacts { get; set; }

    [CommandOption("-o|--out-dir <DIR>")]
    public string? OutDir { get; set; }

    [CommandOption("--stats-json <FILE>")]
    public string? StatsJson { get; set; }

    [CommandOption("--ffmpeg-path <DIR>")]
    public string? FFmpegPath { get; set; }

    [CommandOption("--no-progress")]
    public bool NoProgress { get; set; }
}
