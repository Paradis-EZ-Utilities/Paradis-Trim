using ParadisTrim.Core.Media;

namespace ParadisTrim.Tests;

public class AudioTests : TestBase
{
    [Fact]
    public async Task T07_OpenMp3()
    {
        var media = await MediaProbe.ProbeAsync(TestMedia.Audio(PathIn("song.mp3"), audioArgs: "-c:a libmp3lame -b:a 192k"));
        Assert.InRange(media.Duration.TotalSeconds, 9.95, 10.1);
        Assert.False(media.HasVideo);
        Assert.Equal("mp3", media.AudioStreams[0].Codec);
    }

    [Theory]
    [InlineData("-c:a libmp3lame -b:a 192k")] // constant bitrate
    [InlineData("-c:a libmp3lame -q:a 2")]    // variable bitrate (harder to cut accurately)
    public async Task T09_TrimMp3(string encoding)
    {
        var output = await TrimAsync(TestMedia.Audio(PathIn("song.mp3"), audioArgs: encoding), 3.5, 7.5);

        Assert.Equal("song_trimmed.mp3", Path.GetFileName(output));
        Assert.Equal("mp3", TestMedia.Codecs(output));
        // Players skip the encoder's start-up padding, so check what is actually heard.
        Assert.InRange(TestMedia.DecodedAudioSeconds(output), 3.99, 4.01);
        AssertAudioSpan(output, 3, 7);
    }

    [Fact]
    public async Task T10_TrimWavIsSampleAccurate()
    {
        var source = TestMedia.Audio(PathIn("voice.wav"), audioArgs: "-c:a pcm_s16le");
        var media = await MediaProbe.ProbeAsync(source);
        Assert.InRange(media.Duration.TotalSeconds, 9.999, 10.001);

        var output = await TrimAsync(source, 3.5, 7.5);
        Assert.Equal("pcm_s16le", TestMedia.Codecs(output));
        Assert.InRange(TestMedia.FormatDuration(output), 3.999, 4.001);
        AssertAudioSpan(output, 3, 7);
    }

    [Fact]
    public async Task Wav24Bit()
    {
        var output = await TrimAsync(TestMedia.Audio(PathIn("studio.wav"), audioArgs: "-c:a pcm_s24le"), 1, 2);
        Assert.Equal("pcm_s24le", TestMedia.Codecs(output));
        Assert.InRange(TestMedia.FormatDuration(output), 0.999, 1.001);
    }

    [Fact]
    public async Task T11_TrimM4aAac()
    {
        var output = await TrimAsync(TestMedia.Audio(PathIn("memo.m4a"), audioArgs: "-c:a aac -b:a 256k"), 3.5, 7.5);
        Assert.Equal("aac", TestMedia.Codecs(output));
        Assert.InRange(TestMedia.FormatDuration(output), 3.97, 4.03);
        AssertAudioSpan(output, 3, 7);
    }

    [Fact]
    public async Task TrimM4aAppleLossless()
    {
        var output = await TrimAsync(TestMedia.Audio(PathIn("lossless.m4a"), audioArgs: "-c:a alac"), 3.5, 7.5);
        Assert.Equal("alac", TestMedia.Codecs(output));
        Assert.InRange(TestMedia.FormatDuration(output), 3.999, 4.001);
        AssertAudioSpan(output, 3, 7);
    }

    [Theory]
    [InlineData("song.mp3", "-c:a libmp3lame -b:a 192k", "-id3v2_version 3")]
    [InlineData("song.m4a", "-c:a aac -b:a 192k", "")]
    public async Task AlbumArtIsKept(string name, string encoding, string extra)
    {
        var plain = TestMedia.Audio(PathIn("plain" + Path.GetExtension(name)), audioArgs: encoding);
        var cover = PathIn("cover.png");
        TestMedia.Run(TestMedia.Tool, $"-y -v error -f lavfi -i color=c=orange:s=300x300:d=1 -frames:v 1 \"{cover}\"");
        var source = PathIn(name);
        TestMedia.Run(TestMedia.Tool, $"-y -v error -i \"{plain}\" -i \"{cover}\" -map 0 -map 1 -c copy {extra} -disposition:v attached_pic \"{source}\"");

        var media = await MediaProbe.ProbeAsync(source);
        Assert.NotNull(media.CoverArtStreamIndex);
        Assert.False(media.HasVideo);

        var output = await TrimAsync(source, 2, 5);
        Assert.Contains("png", TestMedia.Codecs(output));
    }

    [Fact]
    public async Task ExtremelyShortMp3()
    {
        var source = TestMedia.Audio(PathIn("click.mp3"), seconds: 0.4, audioArgs: "-c:a libmp3lame");
        var output = await TrimAsync(source, 0.1, 0.3);
        Assert.InRange(TestMedia.FormatDuration(output), 0.15, 0.26);
    }

    [Fact]
    public async Task LongAudioOpensTrimsAndDrawsQuickly()
    {
        var source = TestMedia.Audio(PathIn("podcast.mp3"), seconds: 3600, audioArgs: "-ac 1 -c:a libmp3lame -b:a 32k");
        var media = await MediaProbe.ProbeAsync(source);
        Assert.InRange(media.Duration.TotalSeconds, 3599, 3601);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var peaks = await WaveformGenerator.GenerateAsync(media, 1000, CancellationToken.None);
        Assert.Equal(1000, peaks.Length);
        Assert.True(peaks.Count(p => p > 0.01) > 900);

        var output = await TrimAsync(source, 3590, 3600);
        Assert.InRange(TestMedia.FormatDuration(output), 9.94, 10.06);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(60), $"Took {timer.Elapsed}");
    }

    [Fact]
    public async Task WaveformFollowsTheSound()
    {
        var media = await MediaProbe.ProbeAsync(TestMedia.Audio(PathIn("ramp.wav")));
        var peaks = await WaveformGenerator.GenerateAsync(media, 100, CancellationToken.None);

        Assert.Equal(100, peaks.Length);
        // The test tone gets louder every second, so the end must be louder than the start.
        Assert.True(peaks[95] > peaks[5] * 5, $"{peaks[5]} vs {peaks[95]}");
        Assert.InRange(peaks.Max(), 0.99f, 1.0f);
    }
}
