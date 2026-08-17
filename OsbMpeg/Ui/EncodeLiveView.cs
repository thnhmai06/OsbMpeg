using OsbMpeg.Encoder;
using Spectre.Console;

namespace OsbMpeg.Ui;

public static class EncodeLiveView
{
    public static async Task<EncodeStatistics> RunAsync(EncodePipeline pipeline, bool showProgress, CancellationToken ct = default)
    {
        if (!showProgress)
            return await pipeline.RunAsync(null, ct);

        EncodeStatistics? result = null;

        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Encoding");
                result = await pipeline.RunAsync(p =>
                {
                    task.MaxValue = Math.Max(p.EstimatedTotalFrames, p.FrameIndex);
                    task.Value = p.FrameIndex;
                    task.Description = $"Encoding — frame {p.FrameIndex}/{p.EstimatedTotalFrames} · sprites {p.SpriteCount} · commands {p.CommandCount} · assets {p.AssetCount} ({FormatBytes(p.AssetBytes)})";
                }, ct);
                task.Value = task.MaxValue;
            });

        return result!;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return $"{v:0.##} {units[i]}";
    }
}
