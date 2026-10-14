using System.Text;
using ParadisTrim.Core;
using ParadisTrim.Core.FFmpeg;
using ParadisTrim.Core.Infrastructure;
using ParadisTrim.Core.Media;
using ParadisTrim.Core.Trimming;

namespace ParadisTrim.Tests;

public class SafetyTests : TestBase
{
    [Fact]
    public async Task T12_FileNamesWithSpaces()
    {
        var source = TestMedia.Video(PathIn("My Holiday Folder", "Beach day 2024 final.mp4"), seconds: 4);
        var output = await TrimAsync(source, 1, 3);
        Assert.Equal("Beach day 2024 final_trimmed.mp4", Path.GetFileName(output));
    }

    [Theory]
    [InlineData("Ünïcødé 日本語 🎵", "Café ñoño — テスト.mp3")]
    [InlineData("Ünïcødé 日本語 🎵", "Видео 영상 فيديو.mp4")]
    public async Task T13_UnicodeNamesAndFolders(string folder, string name)
    {
        var path = PathIn(folder, name);
        if (path.EndsWith(".mp3")) TestMedia.Audio(path, seconds: 4, audioArgs: "-c:a libmp3lame");
        else TestMedia.Video(path, seconds: 4);

        var output = await TrimAsync(path, 1, 3);
        Assert.Equal(Path.GetFileNameWithoutExtension(name) + "_trimmed" + Path.GetExtension(name), Path.GetFileName(output));
        Assert.InRange(TestMedia.FormatDuration(output), 1.9, 2.1);
    }

    [Fact]
    public async Task T14_OutputCollisionsGetNumbered()
    {
        var source = TestMedia.Audio(PathIn("Song.mp3"), seconds: 3, audioArgs: "-c:a libmp3lame");
        Assert.Equal("Song_trimmed.mp3", Path.GetFileName(await TrimAsync(source, 0.5, 2)));
        Assert.Equal("Song_trimmed_2.mp3", Path.GetFileName(await TrimAsync(source, 0.5, 2)));
        Assert.Equal("Song_trimmed_3.mp3", Path.GetFileName(await TrimAsync(source, 0.5, 2)));
        // Trimming never replaced an earlier copy.
        Assert.Equal(4, Directory.GetFiles(Path.GetDirectoryName(source)!).Length);
    }

    [Fact]
    public void OutputNamingSkipsFoldersAndExistingFiles()
    {
        var source = PathIn("Song.mp3");
        File.WriteAllText(source, "x");
        Directory.CreateDirectory(PathIn("Song_trimmed.mp3"));
        File.WriteAllText(PathIn("Song_trimmed_2.mp3"), "x");
        Assert.Equal(PathIn("Song_trimmed_3.mp3"), OutputNaming.GetAvailableOutputPath(source));
    }

    [Theory]
    [InlineData("random.mp4", true)]
    [InlineData("empty.mp3", false)]
    [InlineData("notes.wav", false)]
    public async Task T15_InvalidMediaGetsAFriendlyMessage(string name, bool randomBytes)
    {
        var path = PathIn(name);
        if (randomBytes)
        {
            var junk = new byte[50_000];
            new Random(42).NextBytes(junk);
            File.WriteAllBytes(path, junk);
        }
        else if (name.StartsWith("notes")) File.WriteAllText(path, "This is just text pretending to be audio.");
        else File.WriteAllBytes(path, []);

        var error = await Assert.ThrowsAsync<FriendlyException>(() => MediaProbe.ProbeAsync(path));
        Assert.Equal(MediaProbe.UnreadableMessage, error.Message);
    }

    [Fact]
    public async Task UnsupportedKindOfFile()
    {
        var path = PathIn("letter.docx");
        File.WriteAllText(path, "hello");
        var error = await Assert.ThrowsAsync<FriendlyException>(() => MediaProbe.ProbeAsync(path));
        Assert.Contains("MP4, MOV, M4V, MP3, WAV and M4A", error.Message);
    }

    [Fact]
    public void T16_ZeroLengthSelectionIsImpossible()
    {
        var selection = new TrimSelection(TimeSpan.FromSeconds(10));
        Assert.True(selection.IsWholeFile);

        selection.SetStart(TimeSpan.FromSeconds(20));      // past the end
        Assert.Equal(TrimSelection.MinimumLength, selection.Length);

        selection.SetEnd(TimeSpan.Zero);                   // before the start
        Assert.Equal(TrimSelection.MinimumLength, selection.Length);

        selection.Reset();
        selection.SetStart(TimeSpan.FromSeconds(-5));      // before the beginning
        Assert.Equal(TimeSpan.Zero, selection.Start);
        selection.SetEnd(TimeSpan.FromSeconds(99));        // after the end
        Assert.Equal(TimeSpan.FromSeconds(10), selection.End);

        Assert.Throws<FriendlyException>(() => new TrimSelection(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task T16_ServiceRefusesZeroLengthToo()
    {
        var source = TestMedia.Audio(PathIn("a.wav"), seconds: 2);
        var media = await MediaProbe.ProbeAsync(source);
        await Assert.ThrowsAsync<FriendlyException>(() =>
            TrimService.SaveTrimmedCopyAsync(media, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), null, null, CancellationToken.None));
        Assert.Single(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task T17_CancelDuringSave()
    {
        var source = TestMedia.Video(PathIn("big.mp4"), seconds: 40,
            videoArgs: "-c:v libx264 -preset ultrafast -pix_fmt yuv420p", size: "1920x1080");
        var before = TestMedia.Hash(source);
        var media = await MediaProbe.ProbeAsync(source);

        using var cts = new CancellationTokenSource();
        var reported = 0.0;
        var progress = new SynchronousProgress(p =>
        {
            reported = p;
            if (p > 0.05) cts.Cancel(); // the user clicks Cancel part-way through
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TrimService.SaveTrimmedCopyAsync(media, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(39), null, progress, cts.Token));

        Assert.True(reported > 0.05 && reported < 0.9, $"progress was {reported}");
        Assert.Equal(before, TestMedia.Hash(source));
        Assert.Equal([source], Directory.GetFiles(Path.GetDirectoryName(source)!));
    }

    [Fact]
    public async Task T18_ReadOnlyOriginalIsTrimmedAndUntouched()
    {
        var source = TestMedia.Audio(PathIn("locked.m4a"), seconds: 4, audioArgs: "-c:a aac");
        File.SetAttributes(source, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var stamp = File.GetLastWriteTimeUtc(source);

        await TrimAsync(source, 1, 3); // TrimAsync also checks the bytes are identical

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
        Assert.True(File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task T19_FailedSaveLeavesOriginalAndNoLeftovers()
    {
        if (OperatingSystem.IsWindows()) return; // uses a shell script as a broken ffmpeg

        var source = TestMedia.Video(PathIn("clip.mp4"), seconds: 3);
        var before = TestMedia.Hash(source);
        var media = await MediaProbe.ProbeAsync(source);

        // A stand-in ffmpeg that writes half a file and then fails, like a real crash mid-save.
        var fakeDir = PathIn("broken-ffmpeg", "x");
        fakeDir = Path.GetDirectoryName(fakeDir)!;
        var fake = Path.Combine(fakeDir, "ffmpeg");
        File.WriteAllText(fake, "#!/bin/sh\nfor last; do :; done\nhead -c 1000 /dev/urandom > \"$last\"\necho 'Invalid data found when processing input' >&2\nexit 1\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        FFmpegLocator.DirectoryOverride = fakeDir;
        try
        {
            var error = await Assert.ThrowsAsync<FriendlyException>(() =>
                TrimService.SaveTrimmedCopyAsync(media, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), null, null, CancellationToken.None));
            Assert.EndsWith("Your original file hasn't been changed.", error.Message);
            Assert.DoesNotContain("AVERROR", error.Message);
        }
        finally
        {
            FFmpegLocator.DirectoryOverride = null;
        }

        Assert.Equal(before, TestMedia.Hash(source));
        Assert.Equal([source], Directory.GetFiles(Path.GetDirectoryName(source)!));
    }

    [Fact]
    public async Task OriginalDeletedAfterOpening()
    {
        var source = TestMedia.Audio(PathIn("gone.wav"), seconds: 2);
        var media = await MediaProbe.ProbeAsync(source);
        File.Delete(source);

        var error = await Assert.ThrowsAsync<FriendlyException>(() =>
            TrimService.SaveTrimmedCopyAsync(media, TimeSpan.Zero, TimeSpan.FromSeconds(1), null, null, CancellationToken.None));
        Assert.Contains("can't be found", error.Message);
    }

    [Fact]
    public async Task SaveToAnotherFolderWhenAsked()
    {
        var source = TestMedia.Audio(PathIn("in", "a.wav"), seconds: 2);
        var elsewhere = Path.GetDirectoryName(PathIn("out", "x"))!;
        var media = await MediaProbe.ProbeAsync(source);
        var output = await TrimService.SaveTrimmedCopyAsync(media, TimeSpan.Zero, TimeSpan.FromSeconds(1), elsewhere, null, CancellationToken.None);
        Assert.Equal(Path.Combine(elsewhere, "a_trimmed.wav"), output);
    }

    [Fact]
    public void LeftoversFromACrashAreCleanedOnNextStart()
    {
        var leftover = PathIn("Song_trimmed" + TempFileJanitor.TempMarker + ".mp3");
        File.WriteAllText(leftover, "half a file");
        var innocent = PathIn("Song.mp3");
        File.WriteAllText(innocent, "real file");

        TempFileJanitor.Register(leftover);
        TempFileJanitor.Register(innocent); // even if something odd got on the list...
        TempFileJanitor.CleanUpLeftovers();

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(innocent)); // ...files without our marker are never deleted.
    }

    [Fact]
    public void FFmpegErrorsBecomePlainEnglish()
    {
        Assert.Contains("disk is full", TrimService.DescribeFailure("av_interleaved_write_frame(): No space left on device"));
        Assert.Contains("isn't allowed", TrimService.DescribeFailure("out.mp4: Permission denied"));
        Assert.Contains("may be damaged", TrimService.DescribeFailure("[mov,mp4] moov atom not found"));
        Assert.Equal("Something went wrong while saving the trimmed copy.", TrimService.DescribeFailure("AVERROR_INVALIDDATA -1094995529"));
    }

    [Fact]
    public void TimesAreShownSimply()
    {
        Assert.Equal("0:00.0", TimeFormat.Friendly(TimeSpan.Zero));
        Assert.Equal("0:07.5", TimeFormat.Friendly(TimeSpan.FromSeconds(7.5)));
        Assert.Equal("1:00.0", TimeFormat.Friendly(TimeSpan.FromSeconds(59.96)));
        Assert.Equal("1:02:03.4", TimeFormat.Friendly(new TimeSpan(0, 1, 2, 3, 400)));
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests want the callback immediately.</summary>
    private sealed class SynchronousProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
