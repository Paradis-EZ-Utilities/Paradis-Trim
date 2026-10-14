namespace ParadisTrim.Core.Infrastructure;

/// <summary>
/// Keeps a short list of unfinished output files while a save is running.
/// If the app is closed abruptly (crash, power cut), the list survives on disk and
/// <see cref="CleanUpLeftovers"/> deletes those half-written files on the next start.
/// Only files Paradis Trim itself created are ever on this list.
/// </summary>
public static class TempFileJanitor
{
    private static readonly object Gate = new();
    private static string ListFile => Path.Combine(AppPaths.DataFolder, "unfinished-files.txt");

    public static void Register(string path)
    {
        lock (Gate)
        {
            try { File.AppendAllLines(ListFile, [path]); }
            catch (Exception ex) { Log.Warn($"Could not record temp file {path}: {ex.Message}"); }
        }
    }

    /// <summary>Deletes the file (if it still exists) and removes it from the list.</summary>
    public static void Release(string path, bool deleteFile)
    {
        lock (Gate)
        {
            if (deleteFile) TryDelete(path);
            try
            {
                if (!File.Exists(ListFile)) return;
                var remaining = File.ReadAllLines(ListFile)
                    .Where(line => !string.Equals(line, path, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (remaining.Count == 0) File.Delete(ListFile);
                else File.WriteAllLines(ListFile, remaining);
            }
            catch (Exception ex) { Log.Warn($"Could not update temp file list: {ex.Message}"); }
        }
    }

    public static void CleanUpLeftovers()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(ListFile)) return;
                foreach (var path in File.ReadAllLines(ListFile).Where(l => l.Length > 0))
                {
                    // Safety net: only ever delete files that carry our temp marker in their name.
                    if (Path.GetFileName(path).Contains(TempMarker, StringComparison.Ordinal))
                    {
                        TryDelete(path);
                        Log.Info($"Removed unfinished file left from an earlier session: {path}");
                    }
                }
                File.Delete(ListFile);
            }
            catch (Exception ex) { Log.Warn($"Leftover cleanup failed: {ex.Message}"); }
        }
    }

    /// <summary>Every temporary output file name contains this text.</summary>
    public const string TempMarker = ".paradis-trim-unfinished";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn($"Could not delete temp file {path}: {ex.Message}"); }
    }
}
