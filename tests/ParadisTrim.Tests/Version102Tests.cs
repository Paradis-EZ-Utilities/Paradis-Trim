using System.Globalization;
using ParadisTrim.Core;
using ParadisTrim.Core.Media;
using ParadisTrim.Core.Trimming;

namespace ParadisTrim.Tests;

/// <summary>
/// Tests for what changed in 1.0.2: a trimmed video's size stays in proportion to how much was kept.
/// Trims from the very beginning copy the original unchanged; other trims are re-encoded, but never
/// with much more data per second than the original used.
/// </summary>
public class Version102Tests : TestBase
{
    [Fact]
    public void VersionIs102()
    {
        Assert.Equal("1.0.2", AppInfo.Version);
    }

    // ---------------------------------------------------------------- the copy rules

    private static MediaInfo FakeVideo(double duration = 20, double fps = 30, double startTime = 0) => new()
    {
        FilePath = "clip.mp4",
        Duration = TimeSpan.FromSeconds(duration),
        StartTime = TimeSpan.FromSeconds(startTime),
        Video = new VideoStreamInfo(0, "h264", 1280, 720, fps, "yuv420p", 2_000_000),
        AudioStreams = [new AudioStreamInfo(1, "aac", 48000, 2, 128_000)],
    };

    /// <summary>Packets as an x264 file with B-frames stores them: (shown at, decoded at, keyframe).</summary>
    private static readonly VideoPacket[] Head =
    [
        new(0.000, -0.067, 9000, true), new(0.100, -0.033, 900, false), new(0.033, 0.000, 300, false),
    ];

    // Real packet order around 12 s from a test file: the 12.067 picture is stored before 12.033.
    private static readonly VideoPacket[] Tail =
    [
        new(11.933, 11.800, 500, false), new(11.867, 11.833, 300, false), new(11.900, 11.867, 300, false),
        new(11.967, 11.900, 300, false), new(12.000, 11.933, 9000, true), new(12.067, 11.967, 500, false),
        new(12.033, 12.000, 300, false), new(12.133, 12.033, 500, false), new(12.100, 12.067, 300, false),
    ];

    [Theory]
    [InlineData("stream|codec_name=h264|codec_type=video|bit_rate=5393175", "video h264 5393 kbps")]
    [InlineData("stream|codec_name=aac|codec_type=audio|bit_rate=N/A", "audio aac bitrate unknown")]
    [InlineData("format|duration=119.633333", "length 119.633333 s")]
    public void ResultLogLinesAreReadable(string probeLine, string expected)
    {
        Assert.Equal(expected, TrimService.DescribeProbeLine(probeLine));
    }

    [Fact]
    public void TrimFromTheBeginningIsCopiedUpToTheLastNeededPicture()
    {
        var (plan, reason) = TrimStrategy.DecideCopy(FakeVideo(), TimeSpan.Zero, TimeSpan.FromSeconds(12.05), Head, Tail);
        Assert.NotNull(plan);
        // The 12.033 picture is the last one shown before 12.05; it is stored at 12.000, so reading
        // stops between that and the next stored packet (12.033). The 12.067 picture it depends on is kept.
        Assert.Equal(12.0165, plan.VideoReadLimit!.Value.TotalSeconds, 3);
        Assert.Equal(12.05, plan.AudioReadLimit!.Value.TotalSeconds, 3);
        Assert.Equal(1, plan.ExtraFramesAtEnd);
        Assert.Contains("first picture", reason);
    }

    [Fact]
    public void TrimOfTheWholeFileIsCopiedWithoutLimits()
    {
        var (plan, _) = TrimStrategy.DecideCopy(FakeVideo(), TimeSpan.Zero, TimeSpan.FromSeconds(20), Head, []);
        Assert.NotNull(plan);
        Assert.Null(plan.VideoReadLimit);
        Assert.Null(plan.AudioReadLimit);
    }

    [Theory]
    [InlineData(0.5)]   // half a second in
    [InlineData(0.02)]  // even 20 ms in: the first picture would be included although it's before the cut
    public void TrimThatDoesNotStartAtTheBeginningIsReEncoded(double start)
    {
        var (plan, reason) = TrimStrategy.DecideCopy(FakeVideo(), TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(12.05), Head, Tail);
        Assert.Null(plan);
        Assert.Contains("part-way", reason);
    }

    [Fact]
    public void FileThatDoesNotStartWithAKeyframeIsReEncoded()
    {
        VideoPacket[] head = [new(0, 0, 500, false), new(0.033, 0.033, 500, true)];
        var (plan, reason) = TrimStrategy.DecideCopy(FakeVideo(), TimeSpan.Zero, TimeSpan.FromSeconds(12.05), head, Tail);
        Assert.Null(plan);
        Assert.Contains("keyframe", reason);
    }

    [Fact]
    public void CopyThatWouldRunSeveralFramesPastTheEndIsReEncoded()
    {
        // Three pictures shown after 12.0 are stored before the last picture shown before it.
        VideoPacket[] tail =
        [
            new(11.900, 11.800, 300, false), new(12.100, 11.833, 300, false), new(12.067, 11.867, 300, false),
            new(12.033, 11.900, 300, false), new(11.967, 11.933, 300, false), new(12.133, 11.967, 300, false),
        ];
        var (plan, reason) = TrimStrategy.DecideCopy(FakeVideo(), TimeSpan.Zero, TimeSpan.FromSeconds(12.0), Head, tail);
        Assert.Null(plan);
        Assert.Contains("3 frames", reason);
    }

    [Fact]
    public void FileClockThatDoesNotStartAtZeroIsAllowedFor()
    {
        // Same packets, but the file's clock starts at 10 s: the user's 2.05 s is the file's 12.05 s.
        VideoPacket[] head = [new(10.000, 9.933, 9000, true), new(10.033, 9.967, 300, false)];
        var (plan, _) = TrimStrategy.DecideCopy(FakeVideo(startTime: 10), TimeSpan.Zero, TimeSpan.FromSeconds(2.05), head, Tail);
        Assert.NotNull(plan);
        Assert.Equal(2.0165, plan.VideoReadLimit!.Value.TotalSeconds, 3);
        Assert.Equal(2.05, plan.AudioReadLimit!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void PacketLinesAreRead()
    {
        Assert.Equal(new VideoPacket(12.0, 11.933333, 51234, true), TrimStrategy.ParsePacket("12.000000,11.933333,51234,K__"));
        Assert.Equal(new VideoPacket(null, 1.5, 10, false), TrimStrategy.ParsePacket("N/A,1.500000,10,___"));
        Assert.Null(TrimStrategy.ParsePacket(""));
        Assert.Null(TrimStrategy.ParsePacket("garbage"));
    }

    // ---------------------------------------------------------------- the size limit

    [Fact]
    public void SizeLimitFollowsTheKeptPartNotTheWholeFile()
    {
        // A 60 s file; the kept part (20-32 s) is busier than the rest: 500 kB/s = 4 Mbps.
        var packets = Enumerable.Range(0, 1800).Select(i => new VideoPacket(i / 30.0, i / 30.0, i is >= 600 and < 960 ? 500_000 / 30 : 1000, i % 30 == 0)).ToList();
        var budget = TrimStrategy.ChooseBudget(FakeVideo(duration: 60), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(12), packets)!;
        Assert.True(budget.Measured);
        Assert.InRange(budget.SourceBitRate, 3_990_000, 4_010_000);
        Assert.Equal((long)(budget.SourceBitRate * budget.Headroom), budget.MaxRate);
        Assert.Equal(budget.MaxRate * 2, budget.BufferSize); // 2 s of buffer

        // A 1 s clip is measured over 10 s around it (one keyframe more or less would swing it wildly),
        // and gets the one-second minimum buffer.
        var shortClip = TrimStrategy.ChooseBudget(FakeVideo(duration: 60), TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(1), packets)!;
        Assert.Equal((TimeSpan.FromSeconds(20.5), TimeSpan.FromSeconds(30.5)),
            TrimStrategy.MeasureWindow(FakeVideo(duration: 60), TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(1)));
        Assert.InRange(shortClip.SourceBitRate, 3_990_000, 4_010_000);
        Assert.Equal(shortClip.MaxRate, shortClip.BufferSize);

        // Near the ends of the file the 10 s window slides inwards.
        Assert.Equal((TimeSpan.Zero, TimeSpan.FromSeconds(10)),
            TrimStrategy.MeasureWindow(FakeVideo(duration: 60), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        Assert.Equal((TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60)),
            TrimStrategy.MeasureWindow(FakeVideo(duration: 60), TimeSpan.FromSeconds(58), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void SizeLimitFallsBackToTheAverageOrToNone()
    {
        var average = TrimStrategy.ChooseBudget(FakeVideo(), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), [])!;
        Assert.False(average.Measured);
        Assert.Equal(2_000_000, average.SourceBitRate);

        var unknown = FakeVideo();
        unknown = new MediaInfo { FilePath = unknown.FilePath, Duration = unknown.Duration, Video = unknown.Video! with { BitRate = null } };
        Assert.Null(TrimStrategy.ChooseBudget(unknown, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), []));
    }

    [Theory]
    [InlineData(1920, 1080, 30, 4_900_000, 1.10)]  // normal gameplay recording
    [InlineData(1920, 1080, 30, 20_000_000, 1.10)] // generous recording
    [InlineData(426, 240, 23.976, 124_000, 1.21)] // tiny, heavily squeezed web video
    [InlineData(426, 240, 23.976, 50_000, 1.25)]   // even more squeezed: capped at 1.25
    [InlineData(640, 360, 30, 414_720, 1.175)]     // halfway (0.06 bits per pixel)
    [InlineData(1920, 1080, 24, 1_036_000, 1.10)]  // big but simple (cartoon): normal headroom
    public void SqueezedOriginalsGetALittleMoreHeadroom(int width, int height, double fps, long rate, double expected)
    {
        var video = new VideoStreamInfo(0, "h264", width, height, fps, "yuv420p", rate);
        Assert.Equal(expected, TrimStrategy.HeadroomFor(rate, video), 2);
    }

    [Fact]
    public void ReEncodeArgumentsCarryTheLimit()
    {
        var budget = new VideoBudget(2_000_000, true, 2_200_000, 4_400_000, 1.1);
        var args = TrimPlanner.BuildArguments(FakeVideo(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), "out.mp4", budget);
        Assert.Equal("2200000", args[args.ToList().IndexOf("-maxrate") + 1]);
        Assert.Equal("4400000", args[args.ToList().IndexOf("-bufsize") + 1]);
        Assert.Contains("-crf", args);

        var unlimited = TrimPlanner.BuildArguments(FakeVideo(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), "out.mp4");
        Assert.DoesNotContain("-maxrate", unlimited);
    }

    [Fact]
    public void CopyArgumentsReadPictureAndSoundSeparately()
    {
        var plan = new CopyPlan(TimeSpan.FromSeconds(12.0165), TimeSpan.FromSeconds(12.05), 1);
        var args = string.Join(' ', TrimPlanner.BuildCopyArguments(FakeVideo(), plan, "out.mp4"));
        Assert.Contains("-t 12.016500 -i clip.mp4 -t 12.050000 -i clip.mp4 -map 0:0 -map 1:1 -c copy", args);
        Assert.DoesNotContain("libx264", args);
    }

    // ---------------------------------------------------------------- real files

    /// <summary>
    /// A busy, grainy picture squeezed hard by a fast encoder, like a game recording. Re-encoding it
    /// with the quality setting alone made copies about 2.5× bigger per second than the original.
    /// </summary>
    private string GrainyVideo(string name, string size = "640x360", int kbps = 600, double seconds = 10, string audio = "-c:a aac -b:a 96k")
    {
        var path = PathIn(name);
        TestMedia.Run(TestMedia.Tool, string.Create(CultureInfo.InvariantCulture,
            $"-y -v error -f lavfi -i \"testsrc2=s={size}:r=30:d={seconds},noise=alls=12:allf=t\" -f lavfi -i \"sine=f=440:d={seconds}\" " +
            $"-c:v libx264 -preset veryfast -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps * 2}k -g 60 -pix_fmt yuv420p {audio} -shortest \"{path}\""));
        return path;
    }

    [Fact]
    public async Task TrimFromTheBeginningCopiesTheOriginalPicturesExactly()
    {
        var source = GrainyVideo("grainy start.mp4");
        var output = await TrimAsync(source, 0, 6.5);

        // The copy holds the original's own compressed pictures, byte for byte, in the same order.
        var original = PacketSizes(source);
        var copied = PacketSizes(output);
        Assert.InRange(copied.Count, 195, 197); // 6.5 s × 30 fps, plus at most two pictures the last ones depend on
        Assert.Equal(original.Take(copied.Count), copied);
        Assert.Contains("aac", TestMedia.Codecs(output));

        // So the size is in proportion: 65% of the length, about 65% of the file.
        var ratio = new FileInfo(output).Length / (new FileInfo(source).Length * 0.65);
        Assert.InRange(ratio, 0.95, 1.06);
    }

    [Fact]
    public async Task TrimFromTheMiddleStaysInProportion()
    {
        var source = GrainyVideo("grainy middle.mp4");
        var output = await TrimAsync(source, 2, 8);

        var sourceRate = VideoKbps(source);
        var outputRate = VideoKbps(output);
        // 1.0.1 made this about 2.5× the original's rate; now it's the original's rate plus at most ~20%.
        Assert.InRange(outputRate / sourceRate, 0.6, 1.25);
        Assert.InRange(TestMedia.FormatDuration(output), 5.95, 6.05);
        Assert.InRange(new FileInfo(output).Length / (new FileInfo(source).Length * 0.6), 0.6, 1.25);
    }

    [Fact]
    public async Task HighQualityOriginalIsNotPushedUpToTheLimit()
    {
        // A generous original (like a high quality screen recording): the quality setting needs less than
        // the original's rate, so the copy is smaller per second, as in 1.0.1.
        var source = TestMedia.Video(PathIn("generous.mp4"), videoArgs: "-c:v libx264 -preset ultrafast -qp 10 -g 60 -pix_fmt yuv420p");
        var output = await TrimAsync(source, 2, 8);
        Assert.True(VideoKbps(output) < VideoKbps(source) * 0.8, $"{VideoKbps(output)} vs {VideoKbps(source)}");
        AssertVideoSpan(source, output, 2, 7, expectedFrames: 180);
    }

    [Fact]
    public async Task SqueezedSmallVideoDoesNotDoubleInSize()
    {
        // Like a tiny low-quality web video: 240p at about 60 kbps.
        var source = GrainyVideo("tiny.mp4", size: "426x240", kbps: 60, seconds: 20, audio: "-c:a aac -b:a 48k");
        var first = await TrimAsync(source, 0, 10.2);
        var second = await TrimAsync(source, 9.8, 20);

        var total = new FileInfo(first).Length + new FileInfo(second).Length;
        Assert.InRange(total / (double)new FileInfo(source).Length, 0.95, 1.35); // 1.0.1: about 2-5x
    }

    [Fact]
    public async Task HevcIsLimitedToo()
    {
        var source = PathIn("phone.mov");
        TestMedia.Run(TestMedia.Tool, "-y -v error -f lavfi -i \"testsrc2=s=640x360:r=30:d=6,noise=alls=12:allf=t\" -f lavfi -i \"sine=f=440:d=6\" " +
            "-c:v libx265 -preset ultrafast -b:v 500k -tag:v hvc1 -x265-params log-level=error -pix_fmt yuv420p -c:a aac -b:a 96k -shortest \"" + source + "\"");
        var output = await TrimAsync(source, 1, 5);
        Assert.StartsWith("hevc", TestMedia.Codecs(output));
        Assert.InRange(VideoKbps(output) / VideoKbps(source), 0.5, 1.3);
    }

    [Fact]
    public async Task CopiedTrimKeepsSoundAndPictureInSync()
    {
        // Picture brightens and tone gets louder together every second (see TestMedia).
        var source = TestMedia.Video(PathIn("sync copy.mp4"), seconds: 10, videoArgs: "-c:v libx264 -bf 3 -g 60 -pix_fmt yuv420p");
        var output = await TrimAsync(source, 0, 7.4);

        var frames = TestMedia.FrameBrightness(output);
        var secondOf = TestMedia.SecondFinder(source);
        Assert.Equal(0, secondOf(frames[0]));
        Assert.InRange(PacketSizes(output).Count, 222, 224); // 7.4 s = 222 pictures, plus at most two
        AssertAudioSpan(output, 0, 7);
        Assert.InRange(TestMedia.DecodedAudioSeconds(output), 7.38, 7.43);

        // Every brightness step lines up with a loudness step, as in the original.
        var picture = Steps(frames.Select(b => (double)b).ToArray(), 1 / 30.0, 8);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], picture.Select(t => (int)Math.Round(t)).ToArray());
        var sound = Version101Tests.SoundChanges(output);
        Assert.True(sound.Count >= 7, $"sound changes: {sound.Count}");
        for (var i = 0; i < 7; i++) Assert.InRange(Math.Abs(picture[i] - sound[i]), 0, 0.045); // within about a frame
    }

    [Fact]
    public async Task MovFromTheBeginningIsCopiedToo()
    {
        var source = TestMedia.Video(PathIn("camera.mov"), audioArgs: "-c:a pcm_s16le");
        var output = await TrimAsync(source, 0, 4);
        Assert.Equal("h264,pcm_s16le", TestMedia.Codecs(output));
        Assert.Equal(PacketSizes(source).Take(PacketSizes(output).Count), PacketSizes(output));
        AssertVideoSpan(source, output, 0, 3, expectedFrames: 120);
    }

    // ---------------------------------------------------------------- helpers

    private static List<long> PacketSizes(string path) =>
        TestMedia.Run(TestMedia.ProbeTool, $"-v error -select_streams v:0 -show_entries packet=size -of csv=p=0 \"{path}\"")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.Parse(s.TrimEnd(','), CultureInfo.InvariantCulture)).ToList();

    private static double VideoKbps(string path)
    {
        var text = TestMedia.Run(TestMedia.ProbeTool, $"-v error -select_streams v:0 -show_entries stream=bit_rate -of csv=p=0 \"{path}\"").Trim();
        return double.Parse(text.TrimEnd(','), CultureInfo.InvariantCulture) / 1000;
    }

    /// <summary>Times where a series steps up by more than <paramref name="threshold"/>.</summary>
    private static List<double> Steps(double[] values, double interval, double threshold)
    {
        var times = new List<double>();
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] - values[i - 1] > threshold) times.Add(i * interval);
        }
        return times;
    }
}
