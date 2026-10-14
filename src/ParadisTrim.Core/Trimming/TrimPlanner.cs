using ParadisTrim.Core.Media;

namespace ParadisTrim.Core.Trimming;

/// <summary>
/// Decides exactly how ffmpeg should produce the trimmed copy.
///
/// The user never sees any of these choices. The goals, in order:
///   1. The copy starts and ends where the user asked (accuracy).
///   2. It looks and sounds as good as the original (no visible/audible quality loss).
///   3. It stays the same kind of file as the original (MP4 in, MP4 out).
///
/// A video whose kept part starts at the very first picture is copied unchanged (see
/// TrimStrategy and BuildCopyArguments). Any other cut has to be re-encoded, because compressed
/// video can only be cut cleanly at certain points. The re-encode aims for very high quality but
/// is never allowed to use much more data per second than the original did (see VideoBudget),
/// so the copy's size stays in proportion to how much was kept. Lossless audio (WAV, ALAC) is
/// rebuilt sample-for-sample with no loss at all.
///
/// (Copying MP3 frames without re-encoding was tried: it leaves a short burst of silence or
/// a glitch at the start, because each MP3 frame borrows data from the frames before it.)
/// </summary>
public static class TrimPlanner
{
    /// <param name="budget">Size limit for a re-encoded picture, or null for none (quality setting only).</param>
    public static IReadOnlyList<string> BuildArguments(MediaInfo media, TimeSpan start, TimeSpan length, string outputPath,
        VideoBudget? budget = null)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "warning" };

        if (media.HasVideo)
        {
            // Seeking BEFORE the input jumps straight to the right place, which matters for long
            // videos. Because everything is decoded and re-encoded, the cut is still exact.
            if (start > TimeSpan.Zero) args.AddRange(["-ss", TimeFormat.ForFFmpeg(start)]);
            args.AddRange(["-i", media.FilePath, "-t", TimeFormat.ForFFmpeg(length)]);
        }
        else
        {
            // Sound files are decoded from the very beginning and cut with a filter (see AudioCut).
            // Jumping straight into the middle of an MP3 would produce a moment of silence.
            args.AddRange(["-i", media.FilePath]);
        }

        if (media.Video is { } video)
        {
            args.AddRange(["-map", $"0:{video.Index}"]);
            AddVideoEncoding(args, video, budget);
        }
        else if (media.CoverArtStreamIndex is { } coverIndex && media.Extension is ".mp3" or ".m4a")
        {
            // Keep album art exactly as it was.
            args.AddRange(["-map", $"0:{coverIndex}", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        }

        var outputAudio = 0;
        foreach (var audio in media.AudioStreams)
        {
            args.AddRange(["-map", $"0:{audio.Index}"]);
            if (!media.HasVideo)
            {
                args.AddRange([$"-filter:a:{outputAudio}", AudioCut(start, length)]);
            }
            AddAudioEncoding(args, outputAudio++, audio, media);
        }

        AddOutputOptions(args, media, outputPath);
        return args;
    }

    /// <summary>
    /// Copies the picture and sound exactly as they are, from the start of the file up to the read
    /// limits in <paramref name="plan"/>. The file is opened twice so the picture and the sound can
    /// each stop at their own right moment; both keep the original timing, so they stay in sync.
    /// </summary>
    public static IReadOnlyList<string> BuildCopyArguments(MediaInfo media, CopyPlan plan, string outputPath)
    {
        var video = media.Video ?? throw new InvalidOperationException("Copy trimming is only for videos.");
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "warning" };

        if (plan.VideoReadLimit is { } videoLimit) args.AddRange(["-t", Seconds(videoLimit)]);
        args.AddRange(["-i", media.FilePath]);
        if (media.HasAudio)
        {
            if (plan.AudioReadLimit is { } audioLimit) args.AddRange(["-t", Seconds(audioLimit)]);
            args.AddRange(["-i", media.FilePath]);
        }

        args.AddRange(["-map", $"0:{video.Index}"]);
        foreach (var audio in media.AudioStreams) args.AddRange(["-map", $"1:{audio.Index}"]);
        args.AddRange(["-c", "copy"]);

        AddOutputOptions(args, media, outputPath);
        return args;
    }

    /// <summary>A one-line description of what happens to the picture, for the diagnostic log.</summary>
    public static string DescribeVideoEncoding(VideoStreamInfo video, VideoBudget? budget)
    {
        var (encoder, crf) = video.Codec == "hevc" ? ("libx265", 20) : ("libx264", 18);
        var limit = budget is null
            ? "no size limit (original data rate unknown)"
            : $"maxrate {Kbps(budget.MaxRate)} bufsize {Kbps(budget.BufferSize)} (original {Kbps(budget.SourceBitRate)} {(budget.Measured ? "measured for this part" : "file average")} x {budget.Headroom.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)})";
        return $"{encoder} preset medium crf {crf}, {limit}";
    }

    /// <summary>A one-line description of what happens to each sound track, for the diagnostic log.</summary>
    public static string DescribeAudioEncoding(MediaInfo media)
    {
        var args = new List<string>();
        var i = 0;
        foreach (var audio in media.AudioStreams) AddAudioEncoding(args, i++, audio, media);
        var parts = new List<string>();
        for (var k = 0; k + 1 < args.Count; k += 2)
        {
            if (args[k].StartsWith("-c:a", StringComparison.Ordinal)) parts.Add(args[k + 1]);
            else if (args[k].StartsWith("-b:a", StringComparison.Ordinal)) parts[^1] += " " + args[k + 1] + "bps";
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static string Kbps(long bitsPerSecond) => $"{bitsPerSecond / 1000} kbps";

    private static string Seconds(TimeSpan time) =>
        time.TotalSeconds.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);

    private static void AddOutputOptions(List<string> args, MediaInfo media, string outputPath)
    {
        // Keep titles/artist/etc. Chapters are dropped because their times would no longer match.
        args.AddRange(["-map_metadata", "0", "-map_chapters", "-1"]);
        args.AddRange(["-f", MuxerFor(media.Extension)]);

        switch (media.Extension)
        {
            case ".mp4" or ".m4v" or ".mov" or ".m4a":
                // Put the index at the front so the file starts playing immediately everywhere.
                args.AddRange(["-movflags", "+faststart"]);
                break;
            case ".wav":
                // Switch to the extended WAV format automatically if the result is over 4 GB.
                args.AddRange(["-rf64", "auto"]);
                break;
        }

        // Machine-readable progress on standard output.
        args.AddRange(["-progress", "pipe:1", "-nostats"]);
        args.Add(outputPath);
    }

    /// <summary>
    /// Keeps exactly the samples between start and start+length, then restarts the clock at zero.
    /// Unlike a seek, this leaves album art (which sits at time zero) in place.
    /// </summary>
    private static string AudioCut(TimeSpan start, TimeSpan length) =>
        $"atrim=start={TimeFormat.ForFFmpeg(start)}:duration={TimeFormat.ForFFmpeg(length)},asetpts=PTS-STARTPTS";

    private static void AddVideoEncoding(List<string> args, VideoStreamInfo video, VideoBudget? budget)
    {
        var tenBit = video.PixelFormat.Contains("10", StringComparison.Ordinal);

        if (video.Codec == "hevc")
        {
            // HEVC (common from iPhones) stays HEVC, including 10-bit/HDR footage.
            args.AddRange(["-c:v", "libx265", "-preset", "medium", "-crf", "20",
                "-pix_fmt", tenBit ? "yuv420p10le" : "yuv420p",
                "-tag:v", "hvc1", "-x265-params", "log-level=error"]);
        }
        else
        {
            // Everything else becomes H.264, which plays everywhere. CRF 18 is visually lossless.
            args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"]);
        }

        // Keep each picture's exact time from the original. By default ffmpeg rounds times to whole
        // frames, which (with a cut between two frames) shifted the picture up to a frame against the
        // sound and could drop the last frame.
        args.AddRange(["-enc_time_base:v", "demux", "-fps_mode:v", "passthrough"]);

        // The quality setting alone can make a copy 2-3x bigger per second than the original
        // (it faithfully reproduces the original's own compression noise). Limit the data rate to
        // the original's own rate for this part plus a little headroom: the encoder still aims for
        // the quality above, but never spends much more than the original did.
        if (budget is not null)
        {
            args.AddRange(["-maxrate", budget.MaxRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-bufsize", budget.BufferSize.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        // The encoder needs even dimensions. Add at most one pixel of padding to odd-sized video
        // (e.g. some screen recordings) instead of failing. Normal video is never resized.
        if (video.Width % 2 != 0 || video.Height % 2 != 0)
        {
            args.AddRange(["-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2"]);
        }
    }

    private static void AddAudioEncoding(List<string> args, int outputIndex, AudioStreamInfo audio, MediaInfo media)
    {
        var codecOption = $"-c:a:{outputIndex}";

        var mp4Family = media.Extension is ".mp4" or ".m4v" or ".m4a";
        var codec = audio.Codec;

        // Lossless audio is re-created exactly, sample for sample.
        if (codec is "alac" or "flac")
        {
            args.AddRange([codecOption, codec]);
            return;
        }
        if (codec.StartsWith("pcm_", StringComparison.Ordinal) && media.Extension is ".wav" or ".mov")
        {
            args.AddRange([codecOption, codec]);
            return;
        }

        // Lossy audio is re-encoded at about the same bitrate as the original (see ChooseBitrate).
        var encoder = codec switch
        {
            "mp3" when !mp4Family => "libmp3lame",
            "ac3" => "ac3",
            "eac3" => "eac3",
            _ => "aac",
        };
        args.AddRange([codecOption, encoder, $"-b:a:{outputIndex}", $"{ChooseBitrate(audio, encoder) / 1000}k"]);
    }

    // Standard bitrates (in kbps) each encoder works well at. MP3 only allows these exact values.
    private static readonly int[] Mp3Steps = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Ac3Steps = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];
    private static readonly int[] AacSteps = [32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 192, 224, 256, 288, 320, 384, 448, 512, 576, 640];

    /// <summary>
    /// Picks the audio bitrate for a re-encoded sound track, in bits per second.
    ///
    /// Re-encoding can't add quality that the original didn't have, so a higher bitrate than the
    /// source only makes the file bigger. The rule is therefore:
    ///   * Use the source's own bitrate, rounded up to the nearest standard value
    ///     (a 96 kbps original stays 96 kbps; 100 kbps becomes 112 kbps).
    ///   * Never go below a small floor, because very low bitrates lose noticeably more on every
    ///     re-encode (48 kbps mono, 64 kbps stereo, 192 kbps surround).
    ///   * If the source bitrate is unknown, use a safe high-quality default
    ///     (160 kbps stereo AAC, 192 kbps stereo MP3).
    /// </summary>
    internal static long ChooseBitrate(AudioStreamInfo audio, string encoder)
    {
        var channels = Math.Max(1, audio.Channels);
        int[] steps = encoder switch
        {
            "libmp3lame" => Mp3Steps,
            "ac3" or "eac3" => Ac3Steps,
            _ => AacSteps,
        };
        var mono = channels == 1;
        var surround = channels > 2;

        // 1.0.1 used 64/96; 1.0.2 lowered them because lifting a 48 kbps stereo track to 96 kbps
        // doubled the sound's share of small, low-quality videos (see HANDOFF.md).
        int floor = surround ? 192 : mono ? 48 : 64;
        int ceiling = encoder switch
        {
            "libmp3lame" => 320,
            "ac3" or "eac3" => 640,
            _ => surround ? 640 : 320,
        };
        if (encoder == "libmp3lame" && surround) floor = 128; // MP3 is at most stereo anyway.

        int kbps;
        if (audio.BitRate is > 0 and var source)
        {
            // A 3% allowance so measuring noise (e.g. 96.4 kbps) doesn't bump it to the next step.
            var wanted = source / 1000.0 * 0.97;
            kbps = steps.FirstOrDefault(step => step >= wanted, steps[^1]);
        }
        else
        {
            kbps = surround ? 384 : encoder == "libmp3lame" ? (mono ? 128 : 192) : (mono ? 96 : 160);
        }

        kbps = Math.Clamp(kbps, floor, ceiling);
        return kbps * 1000L;
    }

    private static string MuxerFor(string extension) => extension switch
    {
        ".mp4" or ".m4v" => "mp4",
        ".mov" => "mov",
        ".m4a" => "ipod",
        ".mp3" => "mp3",
        ".wav" => "wav",
        _ => throw new InvalidOperationException($"No output format for {extension}"),
    };
}
