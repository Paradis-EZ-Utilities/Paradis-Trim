using ParadisTrim.Core.Media;

namespace ParadisTrim.Tests;

public class VideoTests : TestBase
{
    [Fact]
    public async Task T01_OpenMp4()
    {
        var mp4 = TestMedia.Video(PathIn("clip.mp4"));
        var media = await MediaProbe.ProbeAsync(mp4);

        Assert.InRange(media.Duration.TotalSeconds, 9.95, 10.1);
        Assert.NotNull(media.Video);
        Assert.Equal(640, media.Video!.Width);
        Assert.Equal(360, media.Video.Height);
        Assert.InRange(media.Video.FramesPerSecond, 29.9, 30.1);
        Assert.True(media.HasAudio);
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mov")]
    [InlineData(".m4v")]
    public async Task T04_TrimMiddle(string extension)
    {
        var source = TestMedia.Video(PathIn("clip" + extension));
        var output = await TrimAsync(source, 3.5, 7.5);

        Assert.Equal("clip_trimmed" + extension, Path.GetFileName(output));
        AssertVideoSpan(source, output, firstSecond: 3, lastSecond: 7, expectedFrames: 120);
        AssertAudioSpan(output, 3, 7);
        Assert.Equal("h264,aac", TestMedia.Codecs(output));
    }

    [Fact]
    public async Task T05_TrimBeginningMp4()
    {
        var source = TestMedia.Video(PathIn("clip.mp4"));
        var output = await TrimAsync(source, 2.5, 10);
        AssertVideoSpan(source, output, 2, 9, expectedFrames: 225);
        AssertAudioSpan(output, 2, 9);
    }

    [Fact]
    public async Task T06_TrimEndMp4()
    {
        var source = TestMedia.Video(PathIn("clip.mp4"));
        var output = await TrimAsync(source, 0, 6.5);
        AssertVideoSpan(source, output, 0, 6, expectedFrames: 195, extraFramesAllowed: 2);
        AssertAudioSpan(output, 0, 6);
    }

    [Fact]
    public async Task WholeFileCanBeSaved()
    {
        var source = TestMedia.Video(PathIn("clip.mp4"));
        var output = await TrimAsync(source, 0, 10);
        AssertVideoSpan(source, output, 0, 9, expectedFrames: 300);
    }

    [Fact]
    public async Task HevcTenBitStaysHevcTenBit()
    {
        var source = TestMedia.Video(PathIn("phone.mov"),
            videoArgs: "-c:v libx265 -pix_fmt yuv420p10le -tag:v hvc1 -x265-params log-level=error");
        var output = await TrimAsync(source, 1.5, 4.5);

        Assert.Equal("hevc,aac", TestMedia.Codecs(output));
        Assert.Contains("yuv420p10le", TestMedia.Run(TestMedia.ProbeTool, $"-v error -show_entries stream=pix_fmt -of csv=p=0 \"{output}\""));
        AssertVideoSpan(source, output, 1, 4, expectedFrames: 90);
    }

    [Fact]
    public async Task OddSizedVideoIsPaddedNotRejected()
    {
        var source = TestMedia.Video(PathIn("screen.mp4"), seconds: 4, videoArgs: "-c:v libx264 -pix_fmt yuv444p", size: "641x361");
        var output = await TrimAsync(source, 1, 3);

        Assert.Equal("642,362", TestMedia.Run(TestMedia.ProbeTool, $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{output}\"").Trim());
    }

    [Fact]
    public async Task VideoWithoutSound()
    {
        var source = TestMedia.Video(PathIn("silent.mp4"), audioArgs: "");
        var output = await TrimAsync(source, 2, 5);
        Assert.Equal("h264", TestMedia.Codecs(output));
        AssertVideoSpan(source, output, 2, 4, expectedFrames: 90);
    }

    [Fact]
    public async Task ExtremelyShortVideo()
    {
        var source = TestMedia.Video(PathIn("blink.mp4"), seconds: 0.3);
        var media = await MediaProbe.ProbeAsync(source);
        Assert.InRange(media.Duration.TotalSeconds, 0.25, 0.4);

        var output = await TrimAsync(source, 0.1, 0.25);
        Assert.InRange(TestMedia.FrameBrightness(output).Length, 3, 6);
    }
}
