using System.Diagnostics;
using System.Text;
using ParadisTrim.Core.Infrastructure;

namespace ParadisTrim.Core.FFmpeg;

/// <summary>What a finished ffmpeg/ffprobe run left behind.</summary>
public sealed record ToolResult(int ExitCode, string ErrorOutput)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// The one place where Paradis Trim starts ffmpeg or ffprobe.
/// Every other class describes WHAT it wants (the arguments); this class handles
/// starting the process, reading its output, cancelling it and cleaning it up.
/// </summary>
public static class FFmpegRunner
{
    /// <summary>
    /// Called right after a tool process starts. The Windows app uses this to tie the
    /// process to its own lifetime, so ffmpeg can never keep running after Paradis Trim is gone.
    /// </summary>
    public static Action<Process>? ProcessStarted { get; set; }

    private const int MaxErrorOutputChars = 32_000;

    /// <summary>Runs a tool and hands each line it prints (on standard output) to <paramref name="onOutputLine"/>.</summary>
    public static Task<ToolResult> RunAsync(
        string exePath, IEnumerable<string> arguments, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        return RunCoreAsync(exePath, arguments, async (stdout, ct) =>
        {
            using var reader = new StreamReader(stdout, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                onOutputLine?.Invoke(line);
            }
        }, cancellationToken);
    }

    /// <summary>Runs a tool whose standard output is raw binary data (used for the waveform).</summary>
    public static Task<ToolResult> RunWithBinaryOutputAsync(
        string exePath, IEnumerable<string> arguments, Func<Stream, CancellationToken, Task> readOutput, CancellationToken cancellationToken)
    {
        return RunCoreAsync(exePath, arguments, readOutput, cancellationToken);
    }

    private static async Task<ToolResult> RunCoreAsync(
        string exePath, IEnumerable<string> arguments, Func<Stream, CancellationToken, Task> readOutput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // ffmpeg prints UTF-8. Without this, Windows would decode it with an old code page
            // and non-English file names in messages would turn into question marks.
            StandardErrorEncoding = Encoding.UTF8,
        };
        // ArgumentList quotes every argument correctly, including paths with spaces or Unicode.
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        Log.Info($"Running {Path.GetFileName(exePath)} {string.Join(' ', startInfo.ArgumentList.Select(Quote))}");

        using var process = new Process { StartInfo = startInfo };
        var errorOutput = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errorOutput)
            {
                errorOutput.AppendLine(e.Data);
                // Keep only the end of a very chatty log; the last lines explain failures.
                if (errorOutput.Length > MaxErrorOutputChars) errorOutput.Remove(0, errorOutput.Length - MaxErrorOutputChars);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new FriendlyException(
                "Paradis Trim couldn't start one of its helper programs. Please extract the whole ZIP file again, or check whether security software is blocking it.",
                $"Failed to start {exePath}: {ex}", ex);
        }

        try { ProcessStarted?.Invoke(process); }
        catch (Exception ex) { Log.Warn($"ProcessStarted hook failed: {ex.Message}"); }

        process.BeginErrorReadLine();

        // If the caller cancels, stop the tool straight away.
        await using var registration = cancellationToken.Register(() => KillQuietly(process));

        try
        {
            await readOutput(process.StandardOutput.BaseStream, cancellationToken);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            KillQuietly(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            // Something went wrong while reading output: make sure the tool doesn't linger.
            KillQuietly(process);
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();

        string errors;
        lock (errorOutput) errors = errorOutput.ToString();
        if (process.ExitCode != 0)
        {
            Log.Warn($"{Path.GetFileName(exePath)} exited with code {process.ExitCode}:{Environment.NewLine}{errors.Trim()}");
        }
        return new ToolResult(process.ExitCode, errors);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited on its own in the meantime.
        }
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
