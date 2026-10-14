namespace ParadisTrim.Core.FFmpeg;

/// <summary>
/// Finds the bundled ffmpeg and ffprobe programs.
/// In the release they live in the "ffmpeg" folder next to Paradis Trim.exe.
/// </summary>
public static class FFmpegLocator
{
    /// <summary>Optional override, used by the automated tests and by developers.</summary>
    public const string OverrideVariable = "PARADIS_TRIM_FFMPEG_DIR";

    /// <summary>Lets the automated tests swap in a different set of tools.</summary>
    internal static string? DirectoryOverride { get; set; }

    public static string FFmpegPath => Find("ffmpeg");
    public static string FFprobePath => Find("ffprobe");

    private static string Find(string tool)
    {
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;

        var candidates = new List<string>();
        if (DirectoryOverride is not null) candidates.Add(Path.Combine(DirectoryOverride, exe));
        var overrideDir = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir)) candidates.Add(Path.Combine(overrideDir, exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, exe));

        // Developer convenience: fall back to a copy on the PATH.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length > 0) candidates.Add(Path.Combine(dir, exe));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        throw new FriendlyException(
            "Paradis Trim is missing some of its own files. Please extract the whole ZIP file again and run Paradis Trim from the extracted folder.",
            $"{exe} was not found. Looked in: {string.Join("; ", candidates.Take(3))}");
    }
}
