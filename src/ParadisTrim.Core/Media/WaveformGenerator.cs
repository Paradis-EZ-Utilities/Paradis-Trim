using System.Buffers.Binary;
using ParadisTrim.Core.FFmpeg;

namespace ParadisTrim.Core.Media;

/// <summary>
/// Builds the waveform picture data shown behind the timeline: a list of loudness peaks
/// (0.0 = silence, 1.0 = loudest) evenly spread over the whole file.
/// ffmpeg decodes the sound to plain numbers; we just keep the loudest value in each slice.
/// </summary>
public static class WaveformGenerator
{
    // Decoding at 8 kHz mono is plenty for a picture and keeps long files quick.
    private const int SampleRate = 8000;

    public static async Task<float[]> GenerateAsync(MediaInfo media, int peakCount, CancellationToken cancellationToken)
    {
        if (!media.HasAudio || peakCount <= 0) return [];

        var totalSamples = Math.Max(1, (long)(media.Duration.TotalSeconds * SampleRate));
        var peaks = new float[peakCount];

        var result = await FFmpegRunner.RunWithBinaryOutputAsync(
            FFmpegLocator.FFmpegPath,
            ["-hide_banner", "-nostdin", "-loglevel", "error", "-i", media.FilePath,
             "-map", $"0:{media.AudioStreams[0].Index}", "-ac", "1", "-ar", SampleRate.ToString(),
             "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1"],
            async (stream, ct) =>
            {
                var buffer = new byte[64 * 1024];
                var carry = -1; // A sample split across two reads (rare, but possible).
                long sampleIndex = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    var offset = 0;
                    if (carry >= 0)
                    {
                        Add((short)(carry | (buffer[0] << 8)));
                        carry = -1;
                        offset = 1;
                    }
                    for (; offset + 1 < read; offset += 2)
                    {
                        Add(BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2)));
                    }
                    if (offset < read) carry = buffer[offset];
                }

                void Add(short sample)
                {
                    var slot = (int)Math.Min(peakCount - 1, sampleIndex * peakCount / totalSamples);
                    var level = Math.Abs(sample / 32768f);
                    if (level > peaks[slot]) peaks[slot] = level;
                    sampleIndex++;
                }
            },
            cancellationToken);

        if (!result.Succeeded) return [];

        // Scale so the loudest moment fills the height; quiet recordings stay readable.
        var max = peaks.Max();
        if (max > 0.001f)
        {
            for (var i = 0; i < peaks.Length; i++) peaks[i] /= max;
        }
        return peaks;
    }
}
