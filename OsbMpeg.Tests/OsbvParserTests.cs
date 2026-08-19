using OsbMpeg.Osbv;
using Xunit;

namespace OsbMpeg.Tests;

public class OsbvParserTests
{
    private const string Sample = """
        OsbV: 1
        Sprite,Background,Centre,"bg.png",100,50
         F,0,0,1000,0,1
         M,0,0,500,10,20,30,40
        AnimationVideo,Foreground,Centre,"video1.mp4",200,150,0,30,0,5000
         S,0,0,2000,1,2
         L,0,3
          F,0,0,100,0,1
          L,0,2
           V,0,0,50,1,1,2,2
        Sprite,Foreground,Centre,"overlay.png",300,300
         P,0,0,0,A
         T,HitSoundClap,0,1000,0
          F,0,0,500,0,1
        AnimationVideo,Foreground,Centre,"video2.mp4",400,150,1000

        """;

    [Fact]
    public void ParsesObjectsInDeclarationOrder_PreservingZOrderAcrossTypes()
    {
        var doc = OsbvParser.Parse(Sample.Split('\n').Select(l => l.TrimEnd('\r')));

        Assert.Equal(4, doc.Objects.Count);
        Assert.IsType<OsbvSprite>(doc.Objects[0]);
        Assert.IsType<OsbvAnimationVideo>(doc.Objects[1]);
        Assert.IsType<OsbvSprite>(doc.Objects[2]);
        Assert.IsType<OsbvAnimationVideo>(doc.Objects[3]);
    }

    [Fact]
    public void ParsesNestedLoops()
    {
        var doc = OsbvParser.Parse(Sample.Split('\n').Select(l => l.TrimEnd('\r')));

        var video1 = (OsbvAnimationVideo)doc.Objects[1];
        var outerLoop = Assert.IsType<Ir.SbLoop>(video1.Commands[1]);
        Assert.Equal(2, outerLoop.Children.Count);
        var innerLoop = Assert.IsType<Ir.SbLoop>(outerLoop.Children[1]);
        Assert.Equal(2, innerLoop.Children.Count); // V expands to VectorScaleX + VectorScaleY
    }

    [Fact]
    public void RoundTripsBackToIdenticalText()
    {
        var lines = Sample.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var doc = OsbvParser.Parse(lines);

        var written = OsbvWriter.WriteToString(doc);
        var reparsed = OsbvParser.Parse(written.Split('\n').Select(l => l.TrimEnd('\r')));

        // structural re-parse must match, and the printed text itself must match the
        // hand-written source (no shorthand was used, so nothing is lost on the way out).
        Assert.Equal(doc.Objects.Count, reparsed.Objects.Count);
        var expected = string.Join('\n', lines.Where(l => l.Length > 0)) + "\n";
        Assert.Equal(expected, written.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ParsesTriggerWithChildren()
    {
        var doc = OsbvParser.Parse(Sample.Split('\n').Select(l => l.TrimEnd('\r')));

        var overlay = (OsbvSprite)doc.Objects[2];
        var trigger = Assert.IsType<Ir.SbTrigger>(overlay.Commands[1]);
        Assert.Equal("HitSoundClap", trigger.Name);
        Assert.Equal(0, trigger.Group);
        Assert.Single(trigger.Children);
    }

    [Fact]
    public void RejectsMissingVersionHeader()
    {
        var lines = new[] { "Sprite,Background,Centre,\"bg.png\",0,0" };
        Assert.Throws<OsbvParseException>(() => OsbvParser.Parse(lines));
    }
}
