using System.Text;

namespace ParadisTrim.Core.Infrastructure;

/// <summary>
/// A deliberately tiny diagnostic log. Technical details (FFmpeg messages, exceptions)
/// go here instead of in front of the user. The file is replaced with a fresh one
/// once it grows past a couple of megabytes, so it never piles up.
/// </summary>
public static class Log
{
    private const long MaxSizeBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.LogFolder, "paradis-trim.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxSizeBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }

                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never be the reason the app fails.
        }
    }
}
