using System.Globalization;
using ParadisTrim.Core.FFmpeg;
using ParadisTrim.Core.Media;

namespace ParadisTrim.Core.Trimming;

/// <summary>One compressed picture as stored in the file (times are on the file's own clock, in seconds).</summary>
public sealed record VideoPacket(double? Pts, double? Dts, long Size, bool IsKeyframe);

/// <summary>
/// How to save a video trim by copying the original picture and sound data unchanged.
/// A read limit is how much of the original ffmpeg reads (in decode order), measured from the
/// file's start; null means "to the end of the file".
/// </summary>
public sealed record CopyPlan(TimeSpan? VideoReadLimit, TimeSpan? AudioReadLimit, int ExtraFramesAtEnd);

/// <summary>
/// The size limit for a re-encoded picture: the original's own data rate for the kept part,
/// plus a little headroom.
/// </summary>
/// <param name="SourceBitRate">Bits per second the original spends on the kept part's picture.</param>
/// <param name="Measured">True when measured from the kept part itself; false when it's the whole file's average.</param>
/// <param name="Headroom">MaxRate divided by SourceBitRate.</param>
public sealed record VideoBudget(long SourceBitRate, bool Measured, long MaxRate, long BufferSize, double Headroom);

/// <summary>
/// Decides, before saving a video, whether the trim can be made by copying the original data
/// (no quality change at all, size naturally in proportion) or has to be re-encoded, and if so
/// how much data the new picture may use.
///
/// Why only two ways:
///   * Copying is only exact when the kept part starts on a picture that doesn't depend on earlier
///     ones (a "keyframe"). The first picture of a file always is one; elsewhere keyframes are
///     typically 1–10 seconds apart and moving the user's cut to one is not acceptable.
///   * Re-encoding just the first second or two and copying the rest ("smart rendering") was
///     tested and rejected: the two parts then come from two different encoders with different
///     settings inside one file, which MP4 doesn't properly allow and many players handle badly.
/// </summary>
public static class TrimStrategy
{
    /// <summary>
    /// A re-encoded picture may use at most this much more data per second than the original spent
    /// on the same part. Testing (see HANDOFF.md) showed that the quality setting alone made files
    /// 2–5× bigger, while the original's rate plus about 10% keeps a normally compressed picture
    /// looking the same as the original in normal viewing.
    /// </summary>
    public const double NormalHeadroom = 1.10;

    /// <summary>
    /// Headroom for small originals that are already squeezed very hard (little data for each pixel,
    /// like small low-quality web videos). Every re-encode loses a little, and such a picture has nothing
    /// to spare, so it gets a bit more room. Still far from the 5× the quality setting alone wanted.
    /// </summary>
    public const double StarvedHeadroom = 1.25;

    /// <summary>Pictures up to this size (640×480) can count as squeezed very hard.</summary>
    private const long SmallPicturePixels = 640 * 480;

    /// <summary>Bits per pixel per frame at or above which a picture counts as normally compressed...</summary>
    private const double NormalBitsPerPixel = 0.08;

    /// <summary>...and at or below which it counts as squeezed very hard. In between, the headroom slides.</summary>
    private const double StarvedBitsPerPixel = 0.04;

    /// <summary>How many seconds' worth of data the encoder may borrow for a demanding moment.</summary>
    public const double BufferSeconds = 2.0;

    /// <summary>The smallest buffer, used for very short clips.</summary>
    public const double MinimumBufferSeconds = 1.0;

    /// <summary>
    /// A copied trim can only stop after a picture that later-kept pictures depend on, which can add
    /// up to a couple of frames after the chosen end. More than this and the trim is re-encoded instead.
    /// </summary>
    public const int MaxExtraFramesAtEnd = 2;

    private static readonly HashSet<string> CopyableExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };

    /// <summary>The selection is "from the very beginning" if it starts within this of the first picture.</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromMilliseconds(1);

    /// <summary>Works out a copy plan, or returns null with the reason copying isn't possible.</summary>
    public static async Task<(CopyPlan? Plan, string Reason)> TryPlanCopyAsync(
        MediaInfo media, TimeSpan start, TimeSpan end, CancellationToken cancellationToken)
    {
        if (media.Video is null) return (null, "not a video");
        if (!CopyableExtensions.Contains(media.Extension)) return (null, $"{media.Extension} files are always re-encoded");
        // Quick exit before reading anything: a cut later than the first second can't be at the file's first picture.
        if (start > TimeSpan.FromSeconds(1)) return (null, "the selection starts part-way through");

        var startAbs = media.StartTime.TotalSeconds;
        var head = await ScanAsync(media, $"{Fmt(startAbs)}%+1", cancellationToken);

        IReadOnlyList<VideoPacket> tail = [];
        var toEndOfFile = end >= media.Duration - StartTolerance;
        if (!toEndOfFile)
        {
            var endAbs = startAbs + end.TotalSeconds;
            tail = await ScanAsync(media, $"{Fmt(Math.Max(startAbs, endAbs - 3))}%{Fmt(endAbs + 2)}", cancellationToken);
        }
        return DecideCopy(media, start, end, head, tail);
    }

    /// <summary>
    /// The copy rules, kept separate from the file reading so they can be tested directly.
    /// <paramref name="head"/> is the first packets of the file, <paramref name="tail"/> the packets around
    /// the end of the selection, both in the order they are stored (decode order).
    /// </summary>
    internal static (CopyPlan? Plan, string Reason) DecideCopy(
        MediaInfo media, TimeSpan start, TimeSpan end, IReadOnlyList<VideoPacket> head, IReadOnlyList<VideoPacket> tail)
    {
        var fileStart = media.StartTime.TotalSeconds;

        if (head.Count == 0 || head[0].Pts is null) return (null, "the first picture couldn't be read");
        if (!head[0].IsKeyframe) return (null, "the file doesn't start with a keyframe");
        var firstPicture = head.Where(p => p.Pts is not null).Min(p => p.Pts!.Value) - fileStart;
        if (start.TotalSeconds > Math.Max(0, firstPicture) + StartTolerance.TotalSeconds)
            return (null, "the selection starts part-way through");

        var audioLimit = media.HasAudio ? end : (TimeSpan?)null;
        if (end >= media.Duration - StartTolerance)
        {
            return (new CopyPlan(null, null, 0), "the selection starts at the first picture and runs to the end");
        }

        var endAbs = fileStart + end.TotalSeconds;
        var last = -1;
        for (var i = 0; i < tail.Count; i++)
        {
            if (tail[i].Pts is { } pts && pts < endAbs - 1e-6) last = i;
        }
        if (last < 0 || tail[last].Dts is null) return (null, "the pictures around the end couldn't be read");
        if (last == tail.Count - 1)
        {
            // Nothing after it was read: either the file ends here or the read window was too short.
            return end >= media.Duration - TimeSpan.FromSeconds(1)
                ? (new CopyPlan(null, audioLimit, 0), "the selection starts at the first picture and ends at the last one")
                : (null, "the pictures around the end couldn't be read");
        }

        // Everything stored up to and including the last picture shown before the end must be kept,
        // because it may depend on any of them. Pictures shown after the end that are among those
        // are the "extra frames".
        var extra = 0;
        for (var i = 0; i <= last; i++)
        {
            if (tail[i].Pts is { } pts && pts >= endAbs - 1e-6) extra++;
        }
        if (extra > MaxExtraFramesAtEnd) return (null, $"copying would add {extra} frames after the end");

        if (tail[last + 1].Dts is not { } nextDts || nextDts <= tail[last].Dts!.Value)
            return (null, "the pictures around the end are stored in an unusual order");

        // ffmpeg stops reading a stream at the first packet whose decode time reaches the limit,
        // so put the limit halfway between the last packet we want and the first one we don't.
        var cut = (tail[last].Dts!.Value + nextDts) / 2 - fileStart;
        return (new CopyPlan(TimeSpan.FromSeconds(cut), audioLimit, extra),
            "the selection starts at the first picture of the file");
    }

    /// <summary>
    /// Measures how much picture data the original uses for the kept part and turns it into a size limit.
    /// Falls back to the file's average when the part can't be measured, and to no limit when nothing is known.
    /// </summary>
    public static async Task<VideoBudget?> MeasureBudgetAsync(
        MediaInfo media, TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
    {
        IReadOnlyList<VideoPacket> packets = [];
        try
        {
            var (from, to) = MeasureWindow(media, start, length);
            var fromAbs = media.StartTime.TotalSeconds + from.TotalSeconds;
            packets = await ScanAsync(media, $"{Fmt(fromAbs)}%{Fmt(fromAbs + (to - from).TotalSeconds)}", cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn($"Could not measure the original's picture data: {ex.Message}");
        }
        return ChooseBudget(media, start, length, packets);
    }

    /// <summary>
    /// The stretch of the original to measure. Normally exactly the kept part; for a short part,
    /// about 10 seconds around it, because over a second or two the original's data rate jumps up
    /// and down with each keyframe and wouldn't be a fair measure.
    /// </summary>
    internal static (TimeSpan From, TimeSpan To) MeasureWindow(MediaInfo media, TimeSpan start, TimeSpan length)
    {
        if (length >= MinimumMeasureLength) return (start, start + length);
        var middle = start + length / 2;
        var from = middle - MinimumMeasureLength / 2;
        var to = middle + MinimumMeasureLength / 2;
        if (from < TimeSpan.Zero) (from, to) = (TimeSpan.Zero, MinimumMeasureLength);
        if (to > media.Duration) (from, to) = (media.Duration - MinimumMeasureLength, media.Duration);
        return (from < TimeSpan.Zero ? TimeSpan.Zero : from, to);
    }

    private static readonly TimeSpan MinimumMeasureLength = TimeSpan.FromSeconds(10);

    internal static VideoBudget? ChooseBudget(MediaInfo media, TimeSpan start, TimeSpan length, IReadOnlyList<VideoPacket> packets)
    {
        var (windowStart, windowEnd) = MeasureWindow(media, start, length);
        var from = media.StartTime.TotalSeconds + windowStart.TotalSeconds;
        var to = media.StartTime.TotalSeconds + windowEnd.TotalSeconds;
        var measuredSeconds = to - from;
        long bytes = 0;
        var count = 0;
        foreach (var p in packets)
        {
            if (p.Pts is { } pts && pts >= from - 1e-6 && pts < to - 1e-6)
            {
                bytes += p.Size;
                count++;
            }
        }

        long rate;
        bool measured;
        if (count >= 2 && bytes > 0 && measuredSeconds > 0)
        {
            rate = (long)(bytes * 8 / measuredSeconds);
            measured = true;
        }
        else if (media.Video?.BitRate is > 0 and var average)
        {
            rate = average;
            measured = false;
        }
        else
        {
            return null;
        }

        var headroom = HeadroomFor(rate, media.Video);
        var maxRate = (long)(rate * headroom);
        // The encoder may spend most of a full buffer on its first pictures, so a buffer as long as a
        // short clip would let the clip come out at nearly double the limit. Keep it to a quarter of
        // the clip (at least one second, so a short clip's first full picture isn't starved).
        var bufferSeconds = Math.Clamp(length.TotalSeconds / 4, MinimumBufferSeconds, BufferSeconds);
        return new VideoBudget(rate, measured, maxRate, (long)(maxRate * bufferSeconds), headroom);
    }

    /// <summary>How much more data than the original the re-encoded picture may use (see the constants above).</summary>
    internal static double HeadroomFor(long sourceBitRate, VideoStreamInfo? video)
    {
        if (video is null || video.Width <= 0 || video.Height <= 0 || video.FramesPerSecond <= 0) return NormalHeadroom;
        // Only small pictures: big ones with little data per pixel are usually just simple (cartoons,
        // screen recordings) and look the same with normal headroom; testing showed no benefit for them.
        if ((long)video.Width * video.Height > SmallPicturePixels) return NormalHeadroom;
        var bitsPerPixel = sourceBitRate / ((double)video.Width * video.Height * video.FramesPerSecond);
        var t = Math.Clamp((NormalBitsPerPixel - bitsPerPixel) / (NormalBitsPerPixel - StarvedBitsPerPixel), 0, 1);
        return Math.Round(NormalHeadroom + t * (StarvedHeadroom - NormalHeadroom), 3);
    }

    /// <summary>Lists the picture packets in a time range ("from%to" or "from%+seconds", file clock).</summary>
    internal static async Task<IReadOnlyList<VideoPacket>> ScanAsync(MediaInfo media, string interval, CancellationToken cancellationToken)
    {
        var packets = new List<VideoPacket>();
        var result = await FFmpegRunner.RunAsync(FFmpegLocator.FFprobePath,
            ["-v", "error", "-select_streams", media.Video!.Index.ToString(CultureInfo.InvariantCulture),
             "-read_intervals", interval, "-show_entries", "packet=pts_time,dts_time,size,flags", "-of", "csv=p=0", media.FilePath],
            line => { if (ParsePacket(line) is { } p) packets.Add(p); },
            cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException($"ffprobe failed ({result.ExitCode}): {result.ErrorOutput.Trim()}");
        return packets;
    }

    /// <summary>Reads one "pts,dts,size,flags" line, e.g. "12.000000,11.933333,51234,K__".</summary>
    internal static VideoPacket? ParsePacket(string line)
    {
        var parts = line.Trim().Split(',');
        if (parts.Length < 4) return null;
        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)) return null;
        return new VideoPacket(Num(parts[0]), Num(parts[1]), size, parts[3].StartsWith('K'));

        static double? Num(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }

    private static string Fmt(double seconds) => seconds.ToString("0.000000", CultureInfo.InvariantCulture);
}
