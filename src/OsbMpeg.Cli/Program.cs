using OsbMpeg.Cli.Commands;
using Spectre.Console.Cli;

// Default invocation: osbmpeg <input.osbv> <output.osb> <assets-dir> [--hwaccel MODE].
// decode/bench/probe/inspect are the pre-.osbv whole-canvas commands — kept as hidden
// subcommands (not shown in --help, still callable by name) purely as the regression
// instrument that verifies the compile path against known-good baselines. They're not the
// product surface anymore.
var app = new CommandApp<CompileCommand>();
app.Configure(config =>
{
    config.SetApplicationName("osbmpeg");
    config.Settings.StrictParsing = true; // unknown flags must error, not silently no-op — this bit us once already

    config.AddCommand<DecodeCommand>("decode")
        .WithDescription("Reconstruct a video from a storyboard (.osb + assets).")
        .IsHidden();

    config.AddCommand<BenchCommand>("bench")
        .WithDescription("Encode, decode, and report compression + quality metrics.")
        .IsHidden();

    config.AddCommand<TuneBenchCommand>("tune-bench")
        .WithDescription("Benchmark the auto-tuner: tune a scene while reporting per-window stage timings.")
        .IsHidden();

    config.AddCommand<ProbeCommand>("probe")
        .WithDescription("Print media info for a video file.")
        .IsHidden();

    config.AddCommand<InspectCommand>("inspect")
        .WithDescription("Dump storyboard IR summary for a .osb file.")
        .IsHidden();

    config.AddCommand<CompileCommand>("compile")
        .WithDescription("Compile a .osbv source file into a .osb + assets.")
        .IsHidden(); // same as the default — kept addressable by name for scripts
});

return await app.RunAsync(args);