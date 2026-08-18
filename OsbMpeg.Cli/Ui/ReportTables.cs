using OsbMpeg.Encoder;
using Spectre.Console;

namespace OsbMpeg.Ui;

public static class ReportTables
{
    public static void PrintEncodeSummary(EncodeStatistics s)
    {
        var input = new Table().Border(TableBorder.Rounded).Title("Input");
        input.AddColumn("");
        input.AddColumn("");
        input.HideHeaders();
        input.AddRow("Path", s.InputPath);
        input.AddRow("Resolution", $"{s.Width}x{s.Height}");
        input.AddRow("FPS", s.Fps.ToString("0.###"));
        input.AddRow("Duration", s.Duration.ToString(@"hh\:mm\:ss\.ff"));
        input.AddRow("Frames", s.FrameCount.ToString("N0"));
        AnsiConsole.Write(input);

        var storyboard = new Table().Border(TableBorder.Rounded).Title("Storyboard");
        storyboard.AddColumn("");
        storyboard.AddColumn("");
        storyboard.HideHeaders();
        storyboard.AddRow("Sprites", s.SpriteCount.ToString("N0"));
        storyboard.AddRow("Animations", s.AnimationCount.ToString("N0"));
        storyboard.AddRow("Commands", s.CommandCount.ToString("N0"));
        storyboard.AddRow("Assets", s.AssetCount.ToString("N0"));
        storyboard.AddRow("  of which animation frames", s.AnimationFrameCount.ToString("N0"));
        AnsiConsole.Write(storyboard);

        var compression = new Table().Border(TableBorder.Rounded).Title("Compression");
        compression.AddColumn("");
        compression.AddColumn("");
        compression.HideHeaders();
        compression.AddRow("Raw frames", EncodeLiveView.FormatBytes(s.RawFrameBytes));
        compression.AddRow("Naive (frame-per-sprite, est.)", EncodeLiveView.FormatBytes(s.NaiveEstimatedBytes));
        compression.AddRow("Source file", EncodeLiveView.FormatBytes(s.SourceFileBytes));
        compression.AddRow(".osb", EncodeLiveView.FormatBytes(s.OsbFileBytes));
        compression.AddRow("Assets", EncodeLiveView.FormatBytes(s.AssetBytes));
        compression.AddRow("Reduction vs. raw frames", $"{s.ReductionVsRawFrames:P2}");
        compression.AddRow("Reduction vs. naive", $"{s.ReductionVsNaive:P2}");
        compression.AddRow("Reduction vs. source file", $"{s.ReductionVsSourceFile:P2}");
        compression.AddRow("Encode time", s.EncodeTime.ToString(@"mm\:ss\.ff"));
        AnsiConsole.Write(compression);
    }

    public static void PrintQualityTable(double psnr, double ssim, TimeSpan decodeTime)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("Quality");
        table.AddColumn("");
        table.AddColumn("");
        table.HideHeaders();
        table.AddRow("PSNR", $"{psnr:0.##} dB");
        table.AddRow("SSIM", ssim.ToString("0.####"));
        table.AddRow("Decode time", decodeTime.ToString(@"mm\:ss\.ff"));
        AnsiConsole.Write(table);
    }
}
