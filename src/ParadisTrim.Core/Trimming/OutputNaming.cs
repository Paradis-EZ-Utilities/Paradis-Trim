namespace ParadisTrim.Core.Trimming;

/// <summary>
/// Picks the name of the trimmed copy: "Song.mp3" becomes "Song_trimmed.mp3",
/// then "Song_trimmed_2.mp3", "Song_trimmed_3.mp3"... if those already exist.
/// The "_trimmed" part can be changed in Settings (for example to "_clip").
/// An existing file is never replaced.
/// </summary>
public static class OutputNaming
{
    public const string DefaultSuffix = "_trimmed";

    /// <summary>Longest suffix allowed. Keeps names readable and well clear of Windows' path limits.</summary>
    public const int MaxSuffixLength = 40;

    // The characters Windows doesn't allow in file names. Listed explicitly (rather than asking
    // the operating system) so the rules are the same when the tests run on Linux.
    private static readonly char[] ForbiddenCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <param name="outputFolder">Where to save. Null means "next to the original".</param>
    /// <param name="suffix">Added to the name, e.g. "_trimmed". Null or blank means the default.</param>
    public static string GetAvailableOutputPath(string sourcePath, string? outputFolder = null, string? suffix = null)
    {
        var folder = outputFolder ?? Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var tag = NormalizeSuffix(suffix);

        for (var number = 1; ; number++)
        {
            var candidate = Path.Combine(folder, baseName + (number == 1 ? tag : $"{tag}_{number}") + extension);

            // Also skip names taken by a folder, and never return the source itself.
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !IsSamePath(candidate, sourcePath))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Checks a suffix typed in Settings. Returns null if it's fine (blank is fine: it means the default),
    /// otherwise a short explanation for the user.
    /// </summary>
    public static string? ValidateSuffix(string? suffix)
    {
        var text = suffix?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (text.Length > MaxSuffixLength) return $"Please keep the ending to {MaxSuffixLength} characters or fewer.";

        var bad = text.Where(c => ForbiddenCharacters.Contains(c) || char.IsControl(c)).Distinct().ToArray();
        if (bad.Length > 0)
        {
            var shown = string.Join(" ", bad.Where(c => !char.IsControl(c)));
            return shown.Length > 0
                ? $"File names can't contain these characters: {shown}"
                : "File names can't contain that character.";
        }

        // Windows quietly drops a final dot, which would change the file type.
        if (text.EndsWith('.')) return "The ending can't finish with a dot.";
        return null;
    }

    /// <summary>The suffix that will actually be used: blank or invalid falls back to "_trimmed".</summary>
    public static string NormalizeSuffix(string? suffix)
    {
        var text = suffix?.Trim() ?? "";
        return text.Length == 0 || ValidateSuffix(text) is not null ? DefaultSuffix : text;
    }

    public static bool IsSamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
