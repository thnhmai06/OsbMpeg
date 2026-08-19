using Spectre.Console.Cli;
using System.ComponentModel;

namespace OsbMpeg.Cli;

/// <summary>Dev-verification command for the .osbv compile path (P2/thinnest-path milestone).
/// Not yet the final CLI surface — that's "osbmpeg &lt;input.osbv&gt; &lt;output.osb&gt;
/// &lt;assets-dir&gt; [--hwaccel MODE]" replacing this whole command set once group-transform
/// baking (P5) makes the compile path feature-complete enough to retire encode/bench/decode.</summary>
public sealed class CompileSettings : CommandSettings
{
    [CommandArgument(0, "<input.osbv>")]
    public string Input { get; set; } = "";

    [CommandArgument(1, "<output.osb>")]
    public string Output { get; set; } = "";

    [CommandArgument(2, "<assets-dir>")]
    public string AssetDir { get; set; } = "";
}
