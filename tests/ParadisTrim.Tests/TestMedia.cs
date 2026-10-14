using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ParadisTrim.Tests;

/// <summary>
/// Generates disposable test media with predictable content, and measures results.
///
/// Every generated file is 10 seconds long and changes once per second, so a trimmed
/// copy can be checked for WHERE it starts and ends, not just how long it is:
///  * picture brightness = 20 + 20 × (whole second number)
///  * sound = a 440 Hz tone whose loudness = 0.08 × (second number + 1)
///
/// Generation and measurement always use the test machine's own ffmpeg (TEST_FFMPEG or "ffmpeg"),
/// independent of whichever ffmpeg build Paradis Trim itself is being tested with.
/// </summary>
internal static class TestMedia
{
    public static string Tool => Environment.GetEnvironmentVariable("TEST_FFMPEG") ?? "ffmpeg";
    public static string ProbeTool => Environment.GetEnvironmentVariable("TEST_FFPROBE") ?? "ffprobe";

    private const string Picture = "color=c=black:s=640x360:r=30:d={0},format=yuv444p,geq=lum='20+20*floor(T)':cb=128:cr=128";
    private const string Sound = "aevalsrc='0.08*(floor(t)+1)*sin(2*PI*440*t)':s=48000:d={0}";

    public static string Video(string path, double seconds = 10, string videoArgs = "-c:v libx264 -g 60 -pix_fmt yuv420p",
        string audioArgs = "-c:a aac -b:a 160k", string size = "640x360")
    {
        var picture = string.Format(CultureInfo.InvariantCulture, Picture, seconds).Replace("640x360", size);
        var sound = string.Format(CultureInfo.InvariantCulture, Sound, seconds);
        var audio = audioArgs.Length == 0 ? "" : $"-f lavfi -i \"{sound}\" ";
        Run(Tool, $"-y -v error -f lavfi -i \"{picture}\" {audio}{videoArgs} {audioArgs} -shortest \"{path}\"");
        return path;
    }

    public static string Audio(string path, double seconds = 10, string audioArgs = "")
    {
        var sound = string.Format(CultureInfo.InvariantCulture, Sound, seconds);
        Run(Tool, $"-y -v error -f lavfi -i \"{sound}\" {audioArgs} \"{path}\"");
        return path;
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static double FormatDuration(string path) =>
        double.Parse(Run(ProbeTool, $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{path}\"").Trim(), CultureInfo.InvariantCulture);

    /// <summary>Length of the sound as a player actually hears it (decoded samples ÷ sample rate).</summary>
    public static double DecodedAudioSeconds(string path) =>
        RunBinary(Tool, $"-v error -i \"{path}\" -map 0:a:0 -ac 1 -ar 48000 -f s16le -acodec pcm_s16le pipe:1").Length / 2 / 48000.0;

    public static string Codecs(string path) =>
        Run(ProbeTool, $"-v error -show_entries stream=codec_name -of csv=p=0 \"{path}\"").Trim().Replace("\n", ",");

    /// <summary>Average brightness (0-255) of every video frame, in order.</summary>
    public static byte[] FrameBrightness(string path)
    {
        var bytes = RunBinary(Tool, $"-v error -i \"{path}\" -map 0:v:0 -vf scale=1:1 -f rawvideo -pix_fmt gray pipe:1");
        return bytes;
    }

    /// <summary>
    /// Works out which second of the original each brightness value belongs to,
    /// by measuring the middle frame of every second in the original itself.
    /// </summary>
    public static Func<byte, int> SecondFinder(string original)
    {
        var frames = FrameBrightness(original);
        var reference = Enumerable.Range(0, frames.Length / 30).Select(s => frames[s * 30 + 15]).ToArray();
        return value => Array.IndexOf(reference, reference.MinBy(r => Math.Abs(r - value)));
    }

    /// <summary>Loudest sample (0..1) between <paramref name="from"/> and from+length seconds.</summary>
    public static double PeakLevel(string path, double from, double length)
    {
        var bytes = RunBinary(Tool, string.Create(CultureInfo.InvariantCulture,
            $"-v error -i \"{path}\" -ss {from} -t {length} -map 0:a:0 -ac 1 -f s16le -acodec pcm_s16le pipe:1"));
        double peak = 0;
        for (var i = 0; i + 1 < bytes.Length; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(bytes, i) / 32768.0));
        return peak;
    }

    /// <summary>Loudness expected for the given second of the original.</summary>
    public static double ExpectedLevel(int second) => 0.08 * (second + 1);

    /// <summary>Decodes the whole file; returns ffmpeg's complaints (empty = plays cleanly).</summary>
    public static string DecodeErrors(string path) => Run(Tool, $"-v error -i \"{path}\" -f null -", captureStderr: true).Trim();

    public static string Run(string exe, string args, bool captureStderr = false)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var stderrTask = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var stderr = stderrTask.Result;
        if (p.ExitCode != 0 && !captureStderr) throw new Exception($"{exe} {args} failed: {stderr}");
        return captureStderr ? stderr : stdout;
    }

    public static byte[] RunBinary(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var stderrTask = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception($"{exe} {args} failed: {stderrTask.Result}");
        return ms.ToArray();
    }
}
