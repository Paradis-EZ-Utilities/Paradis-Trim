using System.Globalization;

namespace ParadisTrim.Core.Trimming;

/// <summary>Formats times the way people read them: 1:05.3 or 1:02:05.3 (tenths of a second).</summary>
public static class TimeFormat
{
    public static string Friendly(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        // Round to tenths first so 59.96 s shows as 1:00.0 rather than 0:60.0.
        var tenths = (long)Math.Round(time.TotalMilliseconds / 100, MidpointRounding.AwayFromZero);
        var t = TimeSpan.FromMilliseconds(tenths * 100);
        var hours = (int)t.TotalHours;
        return hours > 0
            ? $"{hours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}"
            : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    /// <summary>Seconds with millisecond precision, in the form ffmpeg expects ("12.345").</summary>
    internal static string ForFFmpeg(TimeSpan time) =>
        time.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
}
