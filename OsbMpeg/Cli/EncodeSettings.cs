using Spectre.Console.Cli;
using System.ComponentModel;

namespace OsbMpeg.Cli;

public sealed class EncodeSettings : CommandSettings
{
    [CommandArgument(0, "<input>")]
    public string Input { get; set; } = "";

    [CommandArgument(1, "<output.osb>")]
    public string Output { get; set; } = "";

    [CommandOption("-y|--overwrite")]
    [Description("Overwrite output without asking.")]
    public bool Overwrite { get; set; }

    [CommandOption("-o|--asset-dir <DIR>")]
    [Description("Directory to write asset PNGs into. Defaults next to output.")]
    public string? AssetDir { get; set; }

    [CommandOption("--name <PREFIX>")]
    [Description("Prefix for generated asset filenames.")]
    public string AssetNamePrefix { get; set; } = "sb";

    [CommandOption("-s|--size <WxH>")]
    [Description("Target canvas resolution, e.g. 1920x1080. Default: 1920x1080.")]
    public string Size { get; set; } = "1920x1080";

    [CommandOption("--keep-source")]
    [Description("Keep source resolution/fps instead of the 1920x1080@30 default.")]
    public bool KeepSource { get; set; }

    [CommandOption("-r|--fps <FPS>")]
    [Description("Sample rate in frames per second. Default: 30.")]
    public double Fps { get; set; } = 30;

    [CommandOption("--ss|--start <TIME>")]
    [Description("Start offset (seconds or HH:MM:SS), forwarded to ffmpeg -ss. Note: type '--ss', not '-ss' — Spectre.Console.Cli only supports single-character single-dash flags.")]
    public string? Start { get; set; }

    [CommandOption("-t|--duration <TIME>")]
    [Description("Duration to encode (ffmpeg time syntax), forwarded to ffmpeg -t.")]
    public string? Duration { get; set; }

    [CommandOption("--vf|--video-filter <FILTERGRAPH>")]
    [Description("Reserved: extra ffmpeg -vf filtergraph applied before analysis. Not yet wired.")]
    public string? VideoFilter { get; set; }

    [CommandOption("--ff:i <ARGS>")]
    [Description("Reserved: raw ffmpeg arguments inserted before -i (input side). Not yet wired.")]
    public string? FFmpegInputArgs { get; set; }

    [CommandOption("--ff:o <ARGS>")]
    [Description("Reserved: raw ffmpeg arguments inserted on the output side. Not yet wired.")]
    public string? FFmpegOutputArgs { get; set; }

    [CommandOption("--ffmpeg-path <DIR>")]
    [Description("Directory containing ffmpeg/ffprobe binaries.")]
    public string? FFmpegPath { get; set; }

    [CommandOption("--quality <SPEC>")]
    [Description("Reserved for the RDO selector (optimizer phase): metric=value, e.g. psnr=40. Not yet wired.")]
    public string Quality { get; set; } = "psnr=35";

    [CommandOption("--preset <PRESET>")]
    [Description("Reserved for the RDO selector (optimizer phase). Not yet wired.")]
    public string Preset { get; set; } = "medium";

    [CommandOption("--tile-size <PX>")]
    [Description("Tile grid cell size in pixels. Default: 64.")]
    public int TileSize { get; set; } = 64;

    [CommandOption("--hash-quant <LEVELS>")]
    [Description("Color quantization levels for run grouping. Also the stored asset's pixel precision (canonical snapshot dedupe) — lowering this trades visual quality for more cross-tile dedupe, it's no longer grouping-only. Default: 32.")]
    public int HashQuantLevels { get; set; } = 32;

    [CommandOption("--gop <FRAMES>")]
    [Description("Bounded segment size for streaming analysis. Default: 300.")]
    public int Gop { get; set; } = 300;

    [CommandOption("--keyframe-interval <FRAMES>")]
    [Description("Reserved for the keyframe planner (optimizer phase).")]
    public int? KeyframeInterval { get; set; }

    [CommandOption("--motion <MODE>")]
    [Description("Reserved: off|global|region. Not yet implemented in the MVP encoder.")]
    public string Motion { get; set; } = "off";

    [CommandOption("--occlusion")]
    [Description("Reserved for the occlusion analyzer (optimizer phase).")]
    public bool Occlusion { get; set; }

    [CommandOption("--max-sprites <N>")]
    [Description("Reserved for the RDO selector (optimizer phase): hard cap on sprite count. Not yet wired.")]
    public int? MaxSprites { get; set; }

    [CommandOption("--max-commands <N>")]
    [Description("Reserved for the RDO selector (optimizer phase): hard cap on command count. Not yet wired.")]
    public int? MaxCommands { get; set; }

    [CommandOption("--max-assets <N>")]
    [Description("Reserved for the RDO selector (optimizer phase): hard cap on distinct asset count. Not yet wired — use --tile-size/--colors/--hash-quant to control asset count today.")]
    public int? MaxAssets { get; set; }

    [CommandOption("--max-asset-pixels <N>")]
    [Description("Max pixel area per merged asset (Ranking Criteria limit). Default: 17000000.")]
    public long MaxAssetPixels { get; set; } = 17_000_000;

    [CommandOption("--asset-format <FMT>")]
    [Description("Reserved: png|jpg. Not yet wired — AssetStore always writes PNG.")]
    public string AssetFormat { get; set; } = "png";

    [CommandOption("--colors <N>")]
    [Description("Palette size for asset PNG quantization. 0 disables quantization. Default: 0.")]
    public int Colors { get; set; }

    [CommandOption("--png-compression <0-9>")]
    [Description("PNG deflate compression level. Default: 6.")]
    public int PngCompression { get; set; } = 6;

    [CommandOption("--loglevel <LEVEL>")]
    [Description("Reserved: quiet|error|warning|info|debug. Not yet wired.")]
    public string LogLevel { get; set; } = "info";

    [CommandOption("--stats-json <FILE>")]
    [Description("Write final EncodeStatistics as JSON to this path.")]
    public string? StatsJson { get; set; }

    [CommandOption("--pack-osz")]
    [Description("Also bundle a minimal .osu + audio + the .osb/assets into a .osz next to the output, ready to drop into osu!.")]
    public bool PackOsz { get; set; }

    [CommandOption("--no-progress")]
    [Description("Disable the live Spectre progress view (for CI/scripts).")]
    public bool NoProgress { get; set; }

    [CommandOption("--benchmark")]
    [Description("Reserved: also decode the result and print quality/performance metrics. Not yet wired — use the `bench` command instead.")]
    public bool Benchmark { get; set; }

    [CommandOption("--integer-frame-delay")]
    [Description("Reserved: snap sample fps so Animation frameDelay is integer ms (stable-safe). Not yet wired — not needed for lazer anyway.")]
    public bool IntegerFrameDelay { get; set; }
}
