using ParadisTrim.Core;
using ParadisTrim.Core.Infrastructure;
using ParadisTrim.Core.Media;
using ParadisTrim.Core.Trimming;

// Some tests swap the ffmpeg folder, which is global, so run tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ParadisTrim.Tests;

/// <summary>Gives every test its own empty folder and a helper that trims while checking the safety rules.</summary>
public abstract class TestBase : IDisposable
{
    protected string Dir { get; }

    protected TestBase()
    {
        Dir = Path.Combine(Path.GetTempPath(), "paradis-trim-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Dir);
        AppPaths.OverrideDataFolder(Path.Combine(Path.GetTempPath(), "paradis-trim-tests", "appdata"));
    }

    protected string PathIn(params string[] parts)
    {
        var path = Path.Combine([Dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>
    /// Trims like the app does and checks, every time, that:
    /// the original is byte-for-byte unchanged, no unfinished files remain,
    /// and the result decodes cleanly from start to finish.
    /// </summary>
    protected static async Task<string> TrimAsync(string source, double start, double end, IProgress<double>? progress = null)
    {
        var before = TestMedia.Hash(source);
        var media = await MediaProbe.ProbeAsync(source);
        var output = await TrimService.SaveTrimmedCopyAsync(media, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), null, progress, CancellationToken.None);

        Assert.Equal(before, TestMedia.Hash(source));
        Assert.True(File.Exists(output));
        Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(output));
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetDirectoryName(output));
        AssertNoUnfinishedFiles(Path.GetDirectoryName(source)!);
        Assert.Equal("", TestMedia.DecodeErrors(output));
        return output;
    }

    protected static void AssertNoUnfinishedFiles(string folder) =>
        Assert.DoesNotContain(Directory.GetFiles(folder), f => f.Contains(TempFileJanitor.TempMarker));

    /// <summary>Checks where a trimmed video starts and ends by looking at its picture.</summary>
    /// <param name="extraFramesAllowed">
    /// A trim from the very beginning is copied, and a copy can only stop after the pictures the last
    /// kept ones depend on: up to two extra frames (under 0.1 s) after the end.
    /// </param>
    protected static void AssertVideoSpan(string original, string output, int firstSecond, int lastSecond, int expectedFrames,
        int extraFramesAllowed = 0)
    {
        var secondOf = TestMedia.SecondFinder(original);
        var frames = TestMedia.FrameBrightness(output);
        Assert.InRange(frames.Length, expectedFrames - 1, expectedFrames + 1 + extraFramesAllowed);
        Assert.Equal(firstSecond, secondOf(frames[0]));
        Assert.Equal(lastSecond, secondOf(frames[^1]));
    }

    /// <summary>Checks where trimmed audio starts and ends by its loudness.</summary>
    protected static void AssertAudioSpan(string output, int firstSecond, int lastSecond)
    {
        var duration = TestMedia.DecodedAudioSeconds(output);
        Assert.Equal(firstSecond, NearestSecond(TestMedia.PeakLevel(output, 0.05, 0.1)));
        Assert.Equal(lastSecond, NearestSecond(TestMedia.PeakLevel(output, duration - 0.15, 0.1)));
    }

    /// <summary>Which second's loudness a measured level is closest to (lossy codecs shift levels by a few %).</summary>
    private static int NearestSecond(double level) =>
        Enumerable.Range(0, 10).MinBy(s => Math.Abs(TestMedia.ExpectedLevel(s) - level));

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }
}
