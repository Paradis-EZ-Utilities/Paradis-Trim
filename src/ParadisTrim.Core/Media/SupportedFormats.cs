namespace ParadisTrim.Core.Media;

/// <summary>
/// The kinds of files Paradis Trim opens. Kept short on purpose: these are the common
/// formats that both trimming and Windows' built-in preview handle reliably.
/// </summary>
public static class SupportedFormats
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v" };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a" };

    public const string FriendlyList = "MP4, MOV, M4V, MP3, WAV and M4A";

    /// <summary>Filter string for the Windows "Open" dialog.</summary>
    public const string OpenDialogFilter =
        "Audio and video files|*.mp4;*.mov;*.m4v;*.mp3;*.wav;*.m4a|" +
        "Video files|*.mp4;*.mov;*.m4v|" +
        "Audio files|*.mp3;*.wav;*.m4a|" +
        "All files|*.*";

    public static bool IsSupported(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path)) || AudioExtensions.Contains(Path.GetExtension(path));
}
