using OsbMpeg.Cli;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("osbmpeg");
    config.Settings.StrictParsing = true; // unknown flags must error, not silently no-op — this bit us once already

    config.AddCommand<EncodeCommand>("encode")
        .WithDescription("Encode a video into an osu! storyboard (.osb + assets).");

    config.AddCommand<DecodeCommand>("decode")
        .WithDescription("Reconstruct a video from a storyboard (.osb + assets).");

    config.AddCommand<BenchCommand>("bench")
        .WithDescription("Encode, decode, and report compression + quality metrics.");

    config.AddCommand<ProbeCommand>("probe")
        .WithDescription("Print media info for a video file.");

    config.AddCommand<InspectCommand>("inspect")
        .WithDescription("Dump storyboard IR summary for a .osb file.");

    config.AddCommand<CompileCommand>("compile")
        .WithDescription("Compile a .osbv source file into a .osb + assets (dev-verification path; no group-transform support yet).");
});

return await app.RunAsync(args);
