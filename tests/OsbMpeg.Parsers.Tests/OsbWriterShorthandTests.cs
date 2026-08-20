using OsbMpeg.Parsers.Ir;
using OsbMpeg.Parsers.Osb;
using Xunit;

namespace OsbMpeg.Parsers.Tests;

public class OsbWriterShorthandTests
{
    private static string[] WriteAndReadCommandLines(List<SbCommand> commands)
    {
        var doc = new SbDocument();
        doc.Add(new SbSprite
        {
            Layer = SbLayer.Background,
            Origin = SbOrigin.Centre,
            X = 0,
            Y = 0,
            Asset = new AssetId("a.png"),
            Commands = commands
        });

        var path = Path.GetTempFileName();
        try
        {
            OsbWriter.Write(doc, path);
            return [.. File.ReadAllLines(path).Where(l => l.StartsWith(' ')).Select(l => l.TrimStart())];
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EqualStartEndTime_LeavesEndTimeFieldBlank()
    {
        var lines = WriteAndReadCommandLines([
            new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 1000, EndMs = 1000, Start = 0.5f, End = 0.5f }
        ]);

        Assert.Equal("F,0,1000,,0.5", Assert.Single(lines));
    }

    [Fact]
    public void DifferentStartEndTime_WritesBothTimeFields()
    {
        var lines = WriteAndReadCommandLines([
            new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = 0, EndMs = 1000, Start = 0, End = 0.5f }
        ]);

        Assert.Equal("F,0,0,1000,0,0.5", Assert.Single(lines));
    }

    [Fact]
    public void EqualStartEndValue_DropsEndValueField()
    {
        var lines = WriteAndReadCommandLines([
            new SbValueCommand { Kind = SbCommandKind.Scale, StartMs = 0, EndMs = 1000, Start = 2f, End = 2f }
        ]);

        Assert.Equal("S,0,0,1000,2", Assert.Single(lines));
    }

    [Fact]
    public void VectorPair_BothAxesUnchanged_DropsEndValues()
    {
        var lines = WriteAndReadCommandLines(
        [
            new SbValueCommand { Kind = SbCommandKind.VectorScaleX, StartMs = 0, EndMs = 1000, Start = 1f, End = 1f },
            new SbValueCommand { Kind = SbCommandKind.VectorScaleY, StartMs = 0, EndMs = 1000, Start = 2f, End = 2f }
        ]);

        Assert.Equal("V,0,0,1000,1,2", Assert.Single(lines));
    }

    [Fact]
    public void VectorPair_OneAxisChanged_KeepsBothEndValues()
    {
        var lines = WriteAndReadCommandLines(
        [
            new SbValueCommand { Kind = SbCommandKind.VectorScaleX, StartMs = 0, EndMs = 1000, Start = 1f, End = 1f },
            new SbValueCommand { Kind = SbCommandKind.VectorScaleY, StartMs = 0, EndMs = 1000, Start = 2f, End = 3f }
        ]);

        Assert.Equal("V,0,0,1000,1,2,1,3", Assert.Single(lines));
    }

    [Fact]
    public void Colour_UnchangedAcrossSpan_DropsEndTriple()
    {
        var lines = WriteAndReadCommandLines([
            new SbColourCommand { StartMs = 0, EndMs = 1000, Start = SbColor.White, End = SbColor.White }
        ]);

        Assert.Equal("C,0,0,1000,255,255,255", Assert.Single(lines));
    }

    [Fact]
    public void FlagCommand_AlwaysSingleFieldForm()
    {
        var lines = WriteAndReadCommandLines([
            new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = 0, EndMs = 1000 }
        ]);

        Assert.Equal("P,0,0,1000,A", Assert.Single(lines));
    }
}