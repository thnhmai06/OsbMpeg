using OsbMpeg.Ir;
using OsuParsers.Storyboards;
using OsuParsers.Storyboards.Commands;
using OsuParsers.Storyboards.Objects;
using OsuCommandType = OsuParsers.Enums.Storyboards.CommandType;

namespace OsbMpeg.Osb;

/// <summary>Converts an OsuParsers-decoded Storyboard into our IR so the same
/// SoftwareStoryboardRenderer can play back a .osb regardless of whether we wrote it
/// ourselves. Enum numeric values were verified to match 1:1 (Origins, Easing, LoopType all
/// share identical member names/order with our Sb* enums), so most mapping is a plain cast.</summary>
public static class OsbReader
{
    public static SbDocument Read(string osbPath)
    {
        var sb = StoryboardDecoderGate.Decode(osbPath);

        var assetRootDir = Path.GetDirectoryName(Path.GetFullPath(osbPath)) ?? ".";
        var placeholderAssets = new AssetStore(assetRootDir, "", "unused"); // decode never writes new assets

        var doc = new SbDocument { Assets = placeholderAssets };

        AddLayer(doc, SbLayer.Background, sb.BackgroundLayer);
        AddLayer(doc, SbLayer.Fail, sb.FailLayer);
        AddLayer(doc, SbLayer.Pass, sb.PassLayer);
        AddLayer(doc, SbLayer.Foreground, sb.ForegroundLayer);
        AddLayer(doc, SbLayer.Overlay, sb.OverlayLayer);

        return doc;
    }

    private static void AddLayer(SbDocument doc, SbLayer layer, List<OsuParsers.Storyboards.Interfaces.IStoryboardObject> objects)
    {
        foreach (var obj in objects)
        {
            switch (obj)
            {
                case StoryboardSprite sprite:
                    doc.Add(new SbSprite
                    {
                        Layer = layer,
                        Origin = (SbOrigin)(int)sprite.Origin,
                        X = sprite.X,
                        Y = sprite.Y,
                        Asset = new AssetId(sprite.FilePath),
                        Commands = ConvertCommands(sprite.Commands),
                    });
                    break;

                case StoryboardAnimation animation:
                    doc.Add(new SbAnimation
                    {
                        Layer = layer,
                        Origin = (SbOrigin)(int)animation.Origin,
                        X = animation.X,
                        Y = animation.Y,
                        BasePath = new AssetId(animation.FilePath),
                        FrameCount = animation.FrameCount,
                        FrameDelayMs = animation.FrameDelay,
                        LoopType = (SbLoopType)(int)animation.LoopType,
                        Commands = ConvertCommands(animation.Commands),
                    });
                    break;
            }
        }
    }


    private static List<SbCommand> ConvertCommands(CommandGroup group)
    {
        var result = new List<SbCommand>();

        foreach (var c in group.Commands)
            AddValueCommand(result, c);

        foreach (var loop in group.Loops)
        {
            result.Add(new SbLoop
            {
                StartMs = loop.LoopStartTime,
                EndMs = loop.LoopStartTime,
                Count = loop.LoopCount,
                Children = ConvertCommands(loop.Commands),
            });
        }

        foreach (var trigger in group.Triggers)
        {
            result.Add(new SbTrigger
            {
                StartMs = trigger.TriggerStartTime,
                EndMs = trigger.TriggerEndTime,
                Name = trigger.TriggerName,
                Group = trigger.GroupNumber,
                Children = ConvertCommands(trigger.Commands),
            });
        }

        return result;
    }

    private static void AddValueCommand(List<SbCommand> result, Command c)
    {
        var easing = (SbEasing)(int)c.Easing;
        switch (c.Type)
        {
            case OsuCommandType.Fade:
                result.Add(new SbValueCommand { Kind = SbCommandKind.Fade, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing, Start = c.StartFloat, End = c.EndFloat });
                break;
            case OsuCommandType.MovementX:
                result.Add(new SbValueCommand { Kind = SbCommandKind.MoveX, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing, Start = c.StartFloat, End = c.EndFloat });
                break;
            case OsuCommandType.MovementY:
                result.Add(new SbValueCommand { Kind = SbCommandKind.MoveY, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing, Start = c.StartFloat, End = c.EndFloat });
                break;
            case OsuCommandType.Movement:
                result.Add(new SbValueCommand { Kind = SbCommandKind.MoveX, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing, Start = c.StartVector.X, End = c.EndVector.X });
                result.Add(new SbValueCommand { Kind = SbCommandKind.MoveY, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing, Start = c.StartVector.Y, End = c.EndVector.Y });
                break;
            case OsuCommandType.Scale:
                result.Add(new SbValueCommand { Kind = SbCommandKind.Scale, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing, Start = c.StartFloat, End = c.EndFloat });
                break;
            case OsuCommandType.VectorScale:
                result.Add(new SbValueCommand { Kind = SbCommandKind.VectorScaleX, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing, Start = c.StartVector.X, End = c.EndVector.X });
                result.Add(new SbValueCommand { Kind = SbCommandKind.VectorScaleY, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing, Start = c.StartVector.Y, End = c.EndVector.Y });
                break;
            case OsuCommandType.Rotation:
                result.Add(new SbValueCommand { Kind = SbCommandKind.Rotate, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing, Start = c.StartFloat, End = c.EndFloat });
                break;
            case OsuCommandType.Colour:
                result.Add(new SbColourCommand
                {
                    StartMs = c.StartTime,
                    EndMs = c.EndTime,
                    Easing = easing,
                    Start = new SbColor(c.StartColour.R, c.StartColour.G, c.StartColour.B),
                    End = new SbColor(c.EndColour.R, c.EndColour.G, c.EndColour.B),
                });
                break;
            case OsuCommandType.FlipHorizontal:
                result.Add(new SbFlagCommand { Kind = SbCommandKind.FlipH, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing });
                break;
            case OsuCommandType.FlipVertical:
                result.Add(new SbFlagCommand { Kind = SbCommandKind.FlipV, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing });
                break;
            case OsuCommandType.BlendingMode:
                result.Add(new SbFlagCommand { Kind = SbCommandKind.Additive, StartMs = c.StartTime, EndMs = c.EndTime, Easing = easing });
                break;
        }
    }
}
