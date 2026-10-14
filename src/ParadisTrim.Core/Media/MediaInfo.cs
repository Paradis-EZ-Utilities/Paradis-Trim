namespace ParadisTrim.Core.Media;

/// <summary>Everything Paradis Trim needs to know about a loaded file.</summary>
public sealed class MediaInfo
{
    public required string FilePath { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>The main picture track, or null for audio files.</summary>
    public VideoStreamInfo? Video { get; init; }

    /// <summary>All sound tracks (usually one).</summary>
    public IReadOnlyList<AudioStreamInfo> AudioStreams { get; init; } = [];

    /// <summary>
    /// The file's own clock reading at its first moment (usually zero). Trim times are measured
    /// from here; ffprobe and ffmpeg report packet times on the file's own clock.
    /// </summary>
    public TimeSpan StartTime { get; init; }

    /// <summary>Size of the file in bytes (0 if unknown). Used for the log and the size estimate.</summary>
    public long FileSize { get; init; }

    /// <summary>Track number of album art embedded in a music file (kept in the trimmed copy), or null.</summary>
    public int? CoverArtStreamIndex { get; init; }

    public bool HasVideo => Video is not null;
    public bool HasAudio => AudioStreams.Count > 0;
    public string FileName => Path.GetFileName(FilePath);
    public string Extension => Path.GetExtension(FilePath).ToLowerInvariant();
}

/// <param name="Index">The track's position inside the file, as ffmpeg numbers it.</param>
public sealed record VideoStreamInfo(
    int Index, string Codec, int Width, int Height, double FramesPerSecond, string PixelFormat, long? BitRate);

public sealed record AudioStreamInfo(
    int Index, string Codec, int SampleRate, int Channels, long? BitRate);
