namespace ParadisTrim.Core.Infrastructure;

/// <summary>
/// Where Paradis Trim keeps its own small files (settings and the diagnostic log).
/// Nothing here ever contains the user's media.
/// </summary>
public static class AppPaths
{
    private static string? _dataFolderOverride;

    /// <summary>%LOCALAPPDATA%\Paradis Trim on Windows.</summary>
    public static string DataFolder
    {
        get
        {
            var folder = _dataFolderOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.ProductName);
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static string LogFolder
    {
        get
        {
            var folder = Path.Combine(DataFolder, "Logs");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    /// <summary>Lets the automated tests keep their files away from the real user folder.</summary>
    internal static void OverrideDataFolder(string folder) => _dataFolderOverride = folder;
}
