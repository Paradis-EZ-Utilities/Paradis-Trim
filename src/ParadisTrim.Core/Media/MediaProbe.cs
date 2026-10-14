using System.Globalization;
using System.Text;
using System.Text.Json;
using ParadisTrim.Core.FFmpeg;
using ParadisTrim.Core.Infrastructure;

namespace ParadisTrim.Core.Media;

/// <summary>
/// Reads a media file's length and tracks using ffprobe.
/// Turns every "can't read this" situation into one friendly message.
/// </summary>
public static class MediaProbe
{
    public const string UnreadableMessage =
        "Paradis Trim couldn't read this file. It may be damaged, or it may use a media format that isn't supported yet.";

    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FriendlyException("That file can't be found. It may have been moved or deleted.", $"File not found: {path}");
        }

        if (!SupportedFormats.IsSupported(path))
        {
            throw new FriendlyException(
                $"Paradis Trim can't open this kind of file yet. It works with {SupportedFormats.FriendlyList} files.",
                $"Unsupported extension: {path}");
        }

        var json = new StringBuilder();
        var result = await FFmpegRunner.RunAsync(
            FFmpegLocator.FFprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            line => json.AppendLine(line),
            cancellationToken);

        if (!result.Succeeded)
        {
            throw new FriendlyException(UnreadableMessage, $"ffprobe failed ({result.ExitCode}) for {path}: {result.ErrorOutput}");
        }

        try
        {
            var info = Parse(path, json.ToString());
            Log.Info($"Opened {path}: {info.Duration}, video={info.Video}, audio={string.Join(", ", info.AudioStreams)}");
            return info;
        }
        catch (FriendlyException) { throw; }
        catch (Exception ex)
        {
            throw new FriendlyException(UnreadableMessage, $"Could not understand ffprobe output for {path}: {ex}", ex);
        }
    }

    internal static MediaInfo Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        VideoStreamInfo? video = null;
        var audio = new List<AudioStreamInfo>();
        int? coverArtIndex = null;
        double longestStream = 0;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = GetString(s, "codec_type");
                var codec = GetString(s, "codec_name") ?? "unknown";
                var index = GetInt(s, "index") ?? 0;
                longestStream = Math.Max(longestStream, GetDouble(s, "duration") ?? 0);

                if (type == "video")
                {
                    // Album art inside MP3/M4A files shows up as a one-picture "video" track.
                    var isPicture = s.TryGetProperty("disposition", out var d)
                        && d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                    if (isPicture)
                    {
                        coverArtIndex ??= index;
                    }
                    else if (video is null)
                    {
                        video = new VideoStreamInfo(
                            index,
                            codec,
                            GetInt(s, "width") ?? 0,
                            GetInt(s, "height") ?? 0,
                            ParseRate(GetString(s, "avg_frame_rate")) ?? ParseRate(GetString(s, "r_frame_rate")) ?? 0,
                            GetString(s, "pix_fmt") ?? "",
                            GetLong(s, "bit_rate"));
                    }
                }
                else if (type == "audio")
                {
                    audio.Add(new AudioStreamInfo(
                        index,
                        codec,
                        GetInt(s, "sample_rate") ?? 0,
                        GetInt(s, "channels") ?? 0,
                        GetLong(s, "bit_rate")));
                }
            }
        }

        double? formatDuration = root.TryGetProperty("format", out var format) ? GetDouble(format, "duration") : null;
        var seconds = formatDuration is > 0 ? formatDuration.Value : longestStream;

        if (video is null && audio.Count == 0)
        {
            throw new FriendlyException(UnreadableMessage, $"No audio or video tracks found in {path}");
        }
        if (seconds <= 0 || double.IsNaN(seconds))
        {
            throw new FriendlyException(UnreadableMessage, $"Could not determine the length of {path}");
        }

        // Some sound files only record the bitrate for the file as a whole. When the sound is the only
        // thing in the file (no picture, no album art), that figure is the sound's bitrate.
        if (video is null && coverArtIndex is null && audio.Count == 1 && audio[0].BitRate is null
            && root.TryGetProperty("format", out var fileFormat) && GetLong(fileFormat, "bit_rate") is > 0 and var fileBitRate)
        {
            audio[0] = audio[0] with { BitRate = fileBitRate };
        }

        double? startSeconds = root.TryGetProperty("format", out var formatForStart) ? GetDouble(formatForStart, "start_time") : null;
        long fileSize = 0;
        try { fileSize = new FileInfo(path).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }

        return new MediaInfo
        {
            FilePath = path,
            Duration = TimeSpan.FromSeconds(seconds),
            StartTime = startSeconds is { } st && double.IsFinite(st) ? TimeSpan.FromSeconds(st) : TimeSpan.Zero,
            FileSize = fileSize,
            Video = video,
            AudioStreams = audio,
            CoverArtStreamIndex = coverArtIndex,
        };
    }

    // ffprobe writes most numbers as strings, e.g. "duration": "12.345000".
    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    private static double? GetDouble(JsonElement e, string name) =>
        double.TryParse(GetString(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? GetInt(JsonElement e, string name) =>
        int.TryParse(GetString(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static long? GetLong(JsonElement e, string name) =>
        long.TryParse(GetString(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

    /// <summary>Turns "30000/1001" into 29.97.</summary>
    private static double? ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return null;
        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0 && num > 0)
        {
            return num / den;
        }
        return null;
    }
}
