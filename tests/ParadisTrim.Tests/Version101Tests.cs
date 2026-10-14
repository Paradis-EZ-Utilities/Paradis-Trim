using System.Globalization;
using System.Reflection;
using ParadisTrim.Core;
using ParadisTrim.Core.Infrastructure;
using ParadisTrim.Core.Media;
using ParadisTrim.Core.Trimming;

namespace ParadisTrim.Tests;

/// <summary>
/// Tests for what changed in 1.0.1: the SAVE TO folder, the file name ending, the remembered
/// settings, the About links, and audio that keeps (instead of inflating) the original's bitrate.
/// </summary>
public class Version101Tests : TestBase
{
    // ---------------------------------------------------------------- where the copy is saved

    [Fact]
    public async Task DefaultSavesNextToTheOriginal()
    {
        var settings = new AppSettings();
        Assert.True(settings.UsesSourceFolder);

        var source = TestMedia.Audio(PathIn("music", "Song.mp3"), seconds: 3, "-c:a libmp3lame -b:a 128k");
        var output = await SaveAsync(source, settings);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(source)!, "Song_trimmed.mp3"), output);
    }

    [Fact]
    public async Task CustomFolderIsUsedAndOriginalIsUntouched()
    {
        var source = TestMedia.Audio(PathIn("music", "Song.wav"), seconds: 3);
        var hash = TestMedia.Hash(source);
        var settings = new AppSettings { OutputFolder = Path.GetDirectoryName(PathIn("Trimmed copies", "x"))! };

        var output = await SaveAsync(source, settings);

        Assert.Equal(Path.Combine(settings.OutputFolder, "Song_trimmed.wav"), output);
        Assert.Equal(hash, TestMedia.Hash(source));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(source)!)); // nothing new next to the original
        AssertNoUnfinishedFiles(settings.OutputFolder);
    }

    [Fact]
    public async Task CustomFolderSurvivesARestartAndCanBeSwitchedBack()
    {
        var folder = Path.GetDirectoryName(PathIn("Mine ünïcode 文件夹", "x"))!;
        ResetSettingsFile();

        var first = AppSettings.Load();
        first.OutputFolder = folder;
        first.Save();

        // "Restart": read everything back from disk.
        var second = AppSettings.Load();
        Assert.Equal(folder, second.OutputFolder);
        Assert.False(second.UsesSourceFolder);

        var source = TestMedia.Audio(PathIn("in", "clip.wav"), seconds: 2);
        Assert.Equal(folder, Path.GetDirectoryName(await SaveAsync(source, second)));

        // Back to "Same folder as source".
        second.OutputFolder = null;
        second.Save();
        var third = AppSettings.Load();
        Assert.True(third.UsesSourceFolder);
        Assert.Equal(Path.GetDirectoryName(source), Path.GetDirectoryName(await SaveAsync(source, third)));
    }

    [Fact]
    public async Task MissingCustomFolderIsReportedAndNothingIsWritten()
    {
        var source = TestMedia.Audio(PathIn("in", "a.wav"), seconds: 2);
        var gone = Path.Combine(Dir, "USB stick that was unplugged");
        var media = await MediaProbe.ProbeAsync(source);

        var error = await Assert.ThrowsAsync<OutputFolderNotWritableException>(() =>
            TrimService.SaveTrimmedCopyAsync(media, TimeSpan.Zero, TimeSpan.FromSeconds(1), gone, null, CancellationToken.None));

        Assert.True(error.IsMissing);
        Assert.Equal(gone, error.Folder);
        Assert.DoesNotContain("Exception", error.Message);
        Assert.False(Directory.Exists(gone)); // we don't quietly re-create it somewhere unexpected
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(source)!));
    }

    [Fact]
    public async Task UnwritableCustomFolderIsReported()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // root ignores folder permissions
        var source = TestMedia.Audio(PathIn("in", "a.wav"), seconds: 2);
        var locked = Path.GetDirectoryName(PathIn("locked", "x"))!;
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var media = await MediaProbe.ProbeAsync(source);
        var error = await Assert.ThrowsAsync<OutputFolderNotWritableException>(() =>
            TrimService.SaveTrimmedCopyAsync(media, TimeSpan.Zero, TimeSpan.FromSeconds(1), locked, null, CancellationToken.None));
        Assert.False(error.IsMissing);
    }

    // ---------------------------------------------------------------- file name ending

    [Fact]
    public async Task CustomSuffixAndCollisionNumbering()
    {
        var source = TestMedia.Audio(PathIn("in", "My Song.mp3"), seconds: 3, "-c:a libmp3lame -b:a 128k");
        var settings = new AppSettings { FileSuffix = "_clip" };

        Assert.Equal("My Song_clip.mp3", Path.GetFileName(await SaveAsync(source, settings)));
        Assert.Equal("My Song_clip_2.mp3", Path.GetFileName(await SaveAsync(source, settings)));
        Assert.Equal("My Song_clip_3.mp3", Path.GetFileName(await SaveAsync(source, settings)));

        // The default ending numbers independently.
        Assert.Equal("My Song_trimmed.mp3", Path.GetFileName(await SaveAsync(source, new AppSettings())));
    }

    [Fact]
    public async Task ExistingFilesAreNeverReplacedInACustomFolder()
    {
        var source = TestMedia.Audio(PathIn("in", "a.wav"), seconds: 2);
        var folder = Path.GetDirectoryName(PathIn("out", "x"))!;
        File.WriteAllText(Path.Combine(folder, "a_cut.wav"), "keep me");
        Directory.CreateDirectory(Path.Combine(folder, "a_cut_2.wav")); // a folder with that name

        var output = await SaveAsync(source, new AppSettings { OutputFolder = folder, FileSuffix = "_cut" });

        Assert.Equal(Path.Combine(folder, "a_cut_3.wav"), output);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(folder, "a_cut.wav")));
    }

    [Theory]
    [InlineData("_cut", null)]
    [InlineData("_clip", null)]
    [InlineData(" _edited ", null)]         // spaces around it are ignored
    [InlineData("", null)]                  // blank means the default
    [InlineData("   ", null)]
    [InlineData("-short version", null)]
    [InlineData("_über ✂", null)]           // non-English letters and emoji are fine on Windows
    [InlineData("_a<b", "<")]
    [InlineData("_a>b", ">")]
    [InlineData("_a:b", ":")]
    [InlineData("_a\"b", "\"")]
    [InlineData("_a/b", "/")]
    [InlineData("_a\\b", "\\")]
    [InlineData("_a|b", "|")]
    [InlineData("_a?b", "?")]
    [InlineData("_a*b", "*")]
    [InlineData("_a\tb", "character")]
    [InlineData("_v2.", "dot")]
    [InlineData("_0123456789012345678901234567890123456789x", "40")]
    public void SuffixValidation(string suffix, string? problemMentions)
    {
        var problem = OutputNaming.ValidateSuffix(suffix);
        if (problemMentions is null)
        {
            Assert.Null(problem);
            var used = OutputNaming.NormalizeSuffix(suffix);
            Assert.Equal(string.IsNullOrWhiteSpace(suffix) ? "_trimmed" : suffix.Trim(), used);
        }
        else
        {
            Assert.NotNull(problem);
            Assert.Contains(problemMentions, problem);
            Assert.Equal("_trimmed", OutputNaming.NormalizeSuffix(suffix)); // never used as-is
        }
    }

    [Fact]
    public async Task InvalidSuffixReachingTheServiceFallsBackSafely()
    {
        var source = TestMedia.Audio(PathIn("in", "a.wav"), seconds: 2);
        var output = await SaveAsync(source, new AppSettings { FileSuffix = "_bad/..\\name" });
        Assert.Equal(Path.Combine(Path.GetDirectoryName(source)!, "a_trimmed.wav"), output);
    }

    // ---------------------------------------------------------------- remembered settings

    [Fact]
    public void AllSettingsSurviveARestart()
    {
        ResetSettingsFile();
        var defaults = AppSettings.Load();
        Assert.Null(defaults.OutputFolder);
        Assert.Equal("_trimmed", defaults.FileSuffix);
        Assert.False(defaults.OpenFolderAfterSave); // off unless the user turns it on

        var folder = Path.GetDirectoryName(PathIn("out", "x"))!;
        defaults.OutputFolder = folder;
        defaults.FileSuffix = "_cut";
        defaults.OpenFolderAfterSave = true;
        defaults.Save();

        var reloaded = AppSettings.Load();
        Assert.Equal(folder, reloaded.OutputFolder);
        Assert.Equal("_cut", reloaded.FileSuffix);
        Assert.True(reloaded.OpenFolderAfterSave);

        reloaded.OpenFolderAfterSave = false;
        reloaded.Save();
        Assert.False(AppSettings.Load().OpenFolderAfterSave);
    }

    [Theory]
    [InlineData("")]                                   // empty file
    [InlineData("{ this is not json")]                 // damaged
    [InlineData("\0\0\0\0")]                           // binary junk (e.g. after a disk error)
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"FileSuffix\": 42, \"OpenFolderAfterSave\": \"yes\"}")] // wrong kinds of values
    public void DamagedSettingsFileFallsBackToDefaults(string content)
    {
        ResetSettingsFile();
        File.WriteAllText(AppPaths.SettingsFile, content);
        var settings = AppSettings.Load();
        Assert.Null(settings.OutputFolder);
        Assert.Equal("_trimmed", settings.FileSuffix);
        Assert.False(settings.OpenFolderAfterSave);

        settings.Save(); // and a good file replaces the damaged one
        Assert.Equal("_trimmed", AppSettings.Load().FileSuffix);
    }

    [Fact]
    public void HandEditedBadValuesAreRepaired()
    {
        ResetSettingsFile();
        File.WriteAllText(AppPaths.SettingsFile,
            "{\"FileSuffix\": \"a?b*\", \"OutputFolder\": \"relative\\\\folder\", \"OpenFolderAfterSave\": true}");
        var settings = AppSettings.Load();
        Assert.Equal("_trimmed", settings.FileSuffix);
        Assert.True(settings.UsesSourceFolder);
        Assert.True(settings.OpenFolderAfterSave); // the good value is kept
    }

    [Fact]
    public void MissingSettingsFileMeansDefaults()
    {
        ResetSettingsFile();
        var settings = AppSettings.Load();
        Assert.True(settings.UsesSourceFolder);
        Assert.Equal("_trimmed", settings.FileSuffix);
        Assert.False(settings.OpenFolderAfterSave);
    }

    [Fact]
    public void SettingsFromVersion100StillLoad()
    {
        ResetSettingsFile();
        File.WriteAllText(AppPaths.SettingsFile,
            "{\"LastFolder\": \"C:\\\\Music\", \"WindowWidth\": 1000, \"WindowHeight\": 720, \"WindowMaximized\": false}");
        var settings = AppSettings.Load();
        Assert.Equal(1000, settings.WindowWidth);
        Assert.True(settings.UsesSourceFolder);
        Assert.Equal("_trimmed", settings.FileSuffix);
    }

    // ---------------------------------------------------------------- About

    [Fact]
    public void AboutLinksAndVersion()
    {
        Assert.Equal("https://github.com/Paradis-EZ-Utilities", AppInfo.MoreUtilitiesUrl);
        Assert.True(Uri.TryCreate(AppInfo.MoreUtilitiesUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https");
        Assert.Equal("", AppInfo.ViewSourceUrl); // not published yet: hidden, not invented

        // The version shown in About matches the version stamped into the program files.
        var stamped = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(AppInfo.Version, stamped.Split('+')[0]);
    }

    // ---------------------------------------------------------------- audio bitrate

    [Theory]
    [InlineData(96_000, 2, "aac", 96)]      // the real-world case: 96 kbps stays 96 kbps (was ~192)
    [InlineData(95_700, 2, "aac", 96)]      // measuring noise doesn't bump it up
    [InlineData(101_000, 2, "aac", 112)]    // otherwise round up to the next standard value
    [InlineData(128_000, 2, "aac", 128)]
    [InlineData(256_000, 2, "aac", 256)]
    [InlineData(500_000, 2, "aac", 320)]    // capped
    [InlineData(48_000, 2, "aac", 64)]      // very low: kept at a sensible minimum (1.0.2: 64, was 96)
    [InlineData(32_000, 1, "aac", 48)]      // (1.0.2: 48, was 64)
    [InlineData(64_000, 2, "aac", 64)]
    [InlineData(null, 2, "aac", 160)]       // unknown: a safe default
    [InlineData(null, 1, "aac", 96)]
    [InlineData(null, 6, "aac", 384)]
    [InlineData(384_000, 6, "aac", 384)]
    [InlineData(128_000, 2, "libmp3lame", 128)]
    [InlineData(150_000, 2, "libmp3lame", 160)] // MP3 only has certain values
    [InlineData(null, 2, "libmp3lame", 192)]
    [InlineData(448_000, 6, "ac3", 448)]
    public void AudioBitrateFollowsTheSource(int? sourceBitRate, int channels, string encoder, int expectedKbps)
    {
        var audio = new AudioStreamInfo(0, "aac", 48000, channels, sourceBitRate);
        Assert.Equal(expectedKbps * 1000L, TrimPlanner.ChooseBitrate(audio, encoder));
    }

    [Theory]
    [InlineData("clip.mp4", "-c:a aac -b:a 96k", 96)]
    [InlineData("clip.m4a", "-c:a aac -b:a 96k", 96)]
    [InlineData("clip.mp3", "-c:a libmp3lame -b:a 128k", 128)]
    public async Task TrimmedAudioIsNotInflated(string name, string audioArgs, int sourceKbps)
    {
        var path = PathIn(name);
        if (name.EndsWith(".mp4")) TestMedia.Video(path, seconds: 10, audioArgs: audioArgs);
        else TestMedia.Audio(path, seconds: 10, audioArgs);

        var output = await TrimAsync(path, 1, 9);
        var kbps = AudioKbps(output);
        // AAC/MP3 encoders land within a few % of the target; the old behaviour gave ~175-190.
        Assert.InRange(kbps, sourceKbps * 0.75, sourceKbps * 1.15);
    }

    // ---------------------------------------------------------------- sync and long video

    [Fact]
    public async Task AudioAndVideoStayInSync()
    {
        // In the test video, the picture gets brighter and the tone louder at the same moment, every second.
        var source = TestMedia.Video(PathIn("sync.mp4"), seconds: 10, audioArgs: "-c:a aac -b:a 96k");
        var output = await TrimAsync(source, 2.5, 8.5);

        var picture = PictureChanges(output);
        var sound = SoundChanges(output);
        Assert.True(picture.Count >= 5, $"picture changes: {picture.Count}");
        Assert.True(sound.Count >= 5, $"sound changes: {sound.Count}");
        for (var i = 0; i < 5; i++)
        {
            // Each change happens half a second, 1.5 s, … into the copy, in sound and picture alike.
            Assert.InRange(picture[i], i + 0.5 - 0.04, i + 0.5 + 0.04);
            Assert.InRange(Math.Abs(picture[i] - sound[i]), 0, 0.045); // within about one frame
        }
    }

    [Fact]
    public async Task LongVideoTrimsNearTheEnd()
    {
        // 40 minutes of small video, made quickly; the cut is near the end, which used to be the slow case.
        var source = PathIn("long video.mp4");
        TestMedia.Run(TestMedia.Tool,
            $"-y -v error -f lavfi -i testsrc2=s=320x180:r=15:d=2400 -f lavfi -i sine=f=440:d=2400 " +
            $"-c:v libx264 -preset ultrafast -g 150 -c:a aac -b:a 96k -shortest \"{source}\"");

        var started = DateTime.UtcNow;
        var output = await TrimAsync(source, 2390, 2397);
        var took = DateTime.UtcNow - started;

        Assert.InRange(TestMedia.FormatDuration(output), 6.9, 7.15);
        Assert.True(took < TimeSpan.FromSeconds(60), $"took {took}");
        Assert.InRange(AudioKbps(output), 70, 112);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<string> SaveAsync(string source, AppSettings settings)
    {
        var before = TestMedia.Hash(source);
        var media = await MediaProbe.ProbeAsync(source);
        var output = await TrimService.SaveTrimmedCopyAsync(media, TimeSpan.FromSeconds(0.5), media.Duration - TimeSpan.FromSeconds(0.5),
            settings.UsesSourceFolder ? null : settings.OutputFolder, null, CancellationToken.None, settings.FileSuffix);
        Assert.Equal(before, TestMedia.Hash(source));
        Assert.True(File.Exists(output));
        return output;
    }

    private static void ResetSettingsFile()
    {
        if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
    }

    private static double AudioKbps(string path)
    {
        var text = TestMedia.Run(TestMedia.ProbeTool,
            $"-v error -select_streams a:0 -show_entries stream=bit_rate:format=bit_rate,duration -of default=nw=1 \"{path}\"");
        var values = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=')).Where(p => p.Length == 2).ToLookup(p => p[0], p => p[1]);
        // Stream bit_rate first (MP4/M4A); otherwise the whole file's (MP3, which has no picture here).
        foreach (var value in values["bit_rate"])
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bps) && bps > 0) return bps / 1000;
        }
        throw new InvalidOperationException("No bitrate in " + text);
    }

    /// <summary>Times (seconds into the file) where the test picture steps to its next brightness.</summary>
    private static List<double> PictureChanges(string path)
    {
        var frames = TestMedia.FrameBrightness(path);
        var times = new List<double>();
        for (var i = 1; i < frames.Length; i++)
        {
            if (frames[i] - frames[i - 1] > 8) times.Add(i / 30.0);
        }
        return times;
    }

    /// <summary>Times where the test tone steps up in loudness, measured on 2 ms windows.</summary>
    internal static List<double> SoundChanges(string path)
    {
        var pcm = TestMedia.RunBinary(TestMedia.Tool, $"-v error -i \"{path}\" -map 0:a:0 -ac 1 -ar 48000 -f s16le -acodec pcm_s16le pipe:1");
        const int window = 96; // 2 ms
        var levels = new List<double>();
        for (var i = 0; i + window * 2 <= pcm.Length; i += window * 2)
        {
            double peak = 0;
            for (var j = 0; j < window * 2; j += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(pcm, i + j) / 32768.0));
            levels.Add(peak);
        }

        // A 440 Hz tone needs a few windows to show its full peak, so compare against the level
        // a little earlier, and report the first window of each clear step up.
        var times = new List<double>();
        for (var k = 6; k < levels.Count; k++)
        {
            var before = levels.Skip(k - 6).Take(4).Max();
            if (levels[k] > before + 0.04 && (times.Count == 0 || k * 0.002 - times[^1] > 0.5)) times.Add(k * 0.002);
        }
        return times;
    }
}
