using OsbMpeg.Osb;

namespace OsbMpeg.Ir;

public sealed class SbDocument
{
    public required AssetStore Assets { get; init; }
    public Dictionary<SbLayer, List<SbObject>> Layers { get; } = new();
    public Dictionary<string, string> Variables { get; } = new();

    public void Add(SbObject obj)
    {
        if (!Layers.TryGetValue(obj.Layer, out var list))
            Layers[obj.Layer] = list = [];
        list.Add(obj);
    }

    public IEnumerable<SbObject> AllObjects => Layers.Values.SelectMany(l => l);

    public int SpriteCount => AllObjects.Count(o => o is SbSprite);
    public int AnimationCount => AllObjects.Count(o => o is SbAnimation);

    public int CommandCount => AllObjects.Sum(o => CountCommands(o.Commands));

    private static int CountCommands(IEnumerable<SbCommand> commands)
    {
        var count = 0;
        foreach (var c in commands)
        {
            count++;
            count += c switch
            {
                SbLoop l => CountCommands(l.Children),
                SbTrigger t => CountCommands(t.Children),
                _ => 0,
            };
        }
        return count;
    }
}
