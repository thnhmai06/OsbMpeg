using System.ComponentModel;
using Spectre.Console.Cli;

namespace OsbMpeg.Cli.Settings;

public sealed class BenchSettings : CommandSettings
{
    [CommandArgument(0, "<input>")] public string Input { get; set; } = "";

    [CommandOption("-s|--size <WxH>")] public string Size { get; set; } = "1920x1080";

    [CommandOption("--keep-source")] public bool KeepSource { get; set; }

    [CommandOption("-r|--fps <FPS>")] public double Fps { get; set; } = 30;

    [CommandOption("--ss|--start <TIME>")] public string? Start { get; set; }

    [CommandOption("-t|--duration <TIME>")]
    public string? Duration { get; set; }

    [CommandOption("--tile-size <PX>")] public int TileSize { get; set; } = 64;

    [CommandOption("--hash-quant <LEVELS>")]
    public int HashQuantLevels { get; set; } = 32;

    [CommandOption("--colors <N>")]
    [Description("Palette size for asset PNG quantization. 0 disables quantization. Default: 0.")]
    public int Colors { get; set; }

    [CommandOption("--raw-snapshot")]
    [Description(
        "A/B flag: store raw pixel snapshots instead of the canonical (hash-quantized) bytes, disabling cross-position dedupe on quantization equality. Default off.")]
    public bool RawSnapshot { get; set; }

    [CommandOption("--min-animation-uniqueness <FRACTION>")]
    [Description(
        "Fraction of distinct content required in a thrashing run before it's promoted to Animation instead of falling back to deduped sprites. 0-1. Default: 0.8.")]
    public double MinAnimationUniqueness { get; set; } = 0.8;

    [CommandOption("--tile-tolerance <N>")]
    [Description(
        "Mean-absolute-error budget (0-255/channel) confirming a hash-proposed run cut against the raw tile before actually cutting. 0 disables (default).")]
    public int TileTolerance { get; set; }

    [CommandOption("--no-quadtree")]
    [Description(
        "Diagnostic: bypass QuadtreeMerger entirely, emitting only base-tile runs. For auditing whether merge helps or hurts asset count.")]
    public bool NoQuadtree { get; set; }

    [CommandOption("--keep-artifacts")]
    [Description("Keep the intermediate .osb, assets, and reconstructed video instead of using a temp dir.")]
    public bool KeepArtifacts { get; set; }

    [CommandOption("-o|--out-dir <DIR>")] public string? OutDir { get; set; }

    [CommandOption("--stats-json <FILE>")] public string? StatsJson { get; set; }

    [CommandOption("--ffmpeg-path <DIR>")] public string? FFmpegPath { get; set; }

    [CommandOption("--no-progress")] public bool NoProgress { get; set; }
}