using System.Text.Json;
using System.Text.Json.Serialization;
using ParadisTrim.Core.Trimming;

namespace ParadisTrim.Core.Infrastructure;

/// <summary>
/// The few things Paradis Trim remembers between runs, stored as a small JSON file in
/// %LOCALAPPDATA%\Paradis Trim\settings.json. A missing or damaged file simply means "use the defaults".
/// </summary>
public sealed class AppSettings
{
    // ----- Chosen by the user (SAVE TO on the main screen, and the Settings window) -----

    /// <summary>Where trimmed copies go. Null means "Same folder as source" (the default).</summary>
    public string? OutputFolder { get; set; }

    /// <summary>Added to the name of the trimmed copy: Song.mp3 → Song_trimmed.mp3.</summary>
    public string FileSuffix { get; set; } = OutputNaming.DefaultSuffix;

    /// <summary>Open File Explorer at the new file after each successful save. Off by default.</summary>
    public bool OpenFolderAfterSave { get; set; }

    // ----- Remembered quietly -----

    public string? LastFolder { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public bool UsesSourceFolder => string.IsNullOrWhiteSpace(OutputFolder);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
                settings.Tidy();
                return settings;
            }
        }
        catch (Exception ex)
        {
            // A damaged settings file is not worth bothering the user about.
            Log.Warn($"Settings could not be read and were reset: {ex.Message}");
        }

        return new AppSettings();
    }

    /// <summary>Repairs values that could only come from a hand-edited or damaged file.</summary>
    private void Tidy()
    {
        FileSuffix = OutputNaming.NormalizeSuffix(FileSuffix);
        if (UsesSourceFolder) OutputFolder = null;
        else if (!Path.IsPathFullyQualified(OutputFolder!) || OutputFolder!.IndexOfAny(['\0', '"', '<', '>', '|', '*', '?']) >= 0)
        {
            OutputFolder = null; // not a usable folder path at all
        }
        if (!double.IsFinite(WindowWidth) || !double.IsFinite(WindowHeight)) WindowWidth = WindowHeight = 0;
    }

    public void Save()
    {
        try
        {
            lock (Gate)
            {
                // Write to a side file first so a crash mid-write can't leave a half-written settings file.
                var temp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(temp, AppPaths.SettingsFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be saved: {ex.Message}");
        }
    }
}
