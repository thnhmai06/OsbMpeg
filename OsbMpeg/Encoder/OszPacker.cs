using System.IO.Compression;
using FFMpegCore;
using OsbMpeg.Osb;
using OsuParsers.Storyboards.Objects;

namespace OsbMpeg.Encoder;

/// <summary>Packs an encoded .osb + assets into a minimal .osz so it can be dropped straight
/// into osu!lazer. Two things are load-bearing and silently break the whole preview if wrong
/// (verified against ppy/osu source, no warning/parse error either way):
///   1. The .osb must be named "{Artist} - {Title} ({Creator}).osb" — WorkingBeatmapCache
///      looks it up by exactly that pattern.
///   2. The .osu's [General] section needs "WidescreenStoryboard: 1" — without it the
///      storyboard layer is 640 wide instead of 853.33 and gets hard-clipped (every sprite
///      layer has Masking=true), losing ~25% of the width with no error.
/// A beatmap also needs an audio file and at least one hit object to import cleanly, so this
/// generates a silent WAV (no codec dependency, unlike mp3) and one harmless circle.</summary>
public static class OszPacker
{
    private const string Artist = "OsbMpeg";
    private const string Creator = "OsbMpeg";
    private const string Version = "Preview";

    public static async Task<string> PackAsync(string osbPath, string assetAbsoluteDir, string assetRelativeDir, TimeSpan duration, string title, string oszOutputPath, CancellationToken ct = default)
    {
        var staging = Path.Combine(Path.GetTempPath(), "osbmpeg_osz_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            var baseName = $"{Artist} - {title} ({Creator})";
            var osuPath = Path.Combine(staging, $"{baseName}.osu");
            var osbDestPath = Path.Combine(staging, $"{baseName}.osb");
            var audioPath = Path.Combine(staging, "audio.wav");

            File.Copy(osbPath, osbDestPath);
            await GenerateSilentAudioAsync(audioPath, duration, ct);
            await File.WriteAllTextAsync(osuPath, BuildOsuFile(title, duration), ct);

            CopyAssets(assetAbsoluteDir, assetRelativeDir, staging);

            ValidateOsu(osuPath);

            if (File.Exists(oszOutputPath))
                File.Delete(oszOutputPath);
            ZipFile.CreateFromDirectory(staging, oszOutputPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            ValidateAnimationFrames(oszOutputPath, osbDestPath);

            return oszOutputPath;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Recursive: AssetStore now nests Animation frames under animations/a{id}/, so a
    /// flat top-level-only copy would silently drop every frame file and leave the zip missing
    /// exactly the assets a repeat of this bug already broke once.
    ///
    /// destDir is built from the caller-supplied assetRelativeDir, not from
    /// assetAbsoluteDir's own folder name — those two only coincide when the encode used the
    /// default asset directory. AssetId paths baked into the .osb always use
    /// EncodeOptions.AssetRelativeDir (default "sb"); inferring the zip folder name from the
    /// disk folder's basename instead breaks the moment someone passes a differently-named
    /// --asset-dir, silently packing assets nobody can find by the declared path.</summary>
    private static void CopyAssets(string assetAbsoluteDir, string assetRelativeDir, string staging)
    {
        if (!Directory.Exists(assetAbsoluteDir))
            return;

        var destDir = Path.Combine(staging, assetRelativeDir.Replace('/', Path.DirectorySeparatorChar));
        foreach (var file in Directory.EnumerateFiles(assetAbsoluteDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(assetAbsoluteDir, file);
            var dest = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    /// <summary>Decodes the packed .osb and, for every Animation object, derives every frame's
    /// expected path the same way osu! itself does (Path.Replace(".", $"{i}.") on the declared
    /// base path) and checks it's actually present as a zip entry. This is the check that would
    /// have caught the original silent-overwrite naming collision and the flat-copy bug above —
    /// both left the .osb referencing frames that either never existed or got clobbered, with
    /// no error anywhere in the encode/pack pipeline.</summary>
    private static void ValidateAnimationFrames(string oszPath, string osbStagingPath)
    {
        var storyboard = StoryboardDecoderGate.Decode(osbStagingPath);

        using var archive = ZipFile.OpenRead(oszPath);
        var entries = new HashSet<string>(
            archive.Entries.Select(e => e.FullName.Replace('\\', '/')),
            StringComparer.Ordinal); // zip entries are case-sensitive regardless of host filesystem

        var missing = new List<string>();
        var animationCount = 0;

        foreach (var layer in new[] { storyboard.BackgroundLayer, storyboard.FailLayer, storyboard.PassLayer, storyboard.ForegroundLayer, storyboard.OverlayLayer })
        {
            foreach (var obj in layer)
            {
                if (obj is not StoryboardAnimation animation)
                    continue;
                animationCount++;

                if (animation.FrameCount <= 0)
                    missing.Add($"{animation.FilePath} (FrameCount={animation.FrameCount})");

                for (var i = 0; i < animation.FrameCount; i++)
                {
                    var framePath = animation.FilePath.Replace(".", $"{i}.").Replace('\\', '/');
                    if (!entries.Contains(framePath))
                        missing.Add(framePath);
                }
            }
        }

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Packed .osz is missing {missing.Count} animation frame file(s) out of {animationCount} Animation object(s) — " +
                $"e.g.: {string.Join(", ", missing.Take(10))}" + (missing.Count > 10 ? ", ..." : ""));
    }

    private static async Task GenerateSilentAudioAsync(string outputPath, TimeSpan duration, CancellationToken ct)
    {
        // clamp to a sane minimum so an empty/near-zero-duration encode still yields a loadable map
        var seconds = Math.Max(1.0, duration.TotalSeconds);
        await FFMpegArguments
            .FromFileInput("anullsrc=r=44100:cl=stereo", verifyExists: false, o => o.ForceFormat("lavfi"))
            .OutputToFile(outputPath, true, o => o.WithDuration(TimeSpan.FromSeconds(seconds)))
            .ProcessAsynchronously(true);
    }

    private static string BuildOsuFile(string title, TimeSpan duration)
    {
        var lengthMs = (int)Math.Max(1000, duration.TotalMilliseconds);
        return $"""
            osu file format v14

            [General]
            AudioFilename: audio.wav
            AudioLeadIn: 0
            PreviewTime: -1
            Countdown: 0
            SampleSet: Normal
            StackLeniency: 0.7
            Mode: 0
            LetterboxInBreaks: 0
            WidescreenStoryboard: 1

            [Editor]
            DistanceSpacing: 1
            BeatDivisor: 4
            GridSize: 4
            TimelineZoom: 1

            [Metadata]
            Title:{title}
            TitleUnicode:{title}
            Artist:{Artist}
            ArtistUnicode:{Artist}
            Creator:{Creator}
            Version:{Version}
            Source:
            Tags:osbmpeg
            BeatmapID:0
            BeatmapSetID:-1

            [Difficulty]
            HPDrainRate:5
            CircleSize:5
            OverallDifficulty:5
            ApproachRate:5
            SliderMultiplier:1.4
            SliderTickRate:1

            [Events]
            //Background and Video events
            //Storyboard Layer 0 (Background)
            //Storyboard Layer 1 (Fail)
            //Storyboard Layer 2 (Pass)
            //Storyboard Layer 3 (Foreground)
            //Storyboard Layer 4 (Overlay)
            //Storyboard Sound Samples

            [TimingPoints]
            0,500,4,2,0,100,1,0

            [HitObjects]
            256,192,0,1,0,0:0:0:0:
            256,192,{lengthMs},1,0,0:0:0:0:

            """;
    }

    private static void ValidateOsu(string osuPath)
    {
        var beatmap = OsuParsers.Decoders.BeatmapDecoder.Decode(osuPath);
        if (!beatmap.GeneralSection.WidescreenStoryboard)
            throw new InvalidOperationException("Generated .osu is missing WidescreenStoryboard:1 — the packed .osz would render the storyboard hard-clipped to 4:3 in osu!.");
    }
}
