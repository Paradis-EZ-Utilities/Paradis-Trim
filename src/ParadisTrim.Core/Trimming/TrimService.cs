using System.Globalization;
using ParadisTrim.Core.FFmpeg;
using ParadisTrim.Core.Infrastructure;
using ParadisTrim.Core.Media;

namespace ParadisTrim.Core.Trimming;

/// <summary>
/// Saves the trimmed copy. The safety rules:
///  * The original file is only ever READ, never written, moved or deleted.
///  * ffmpeg writes to a clearly-named unfinished file first. Only a complete, checked
///    result is renamed to the final name, so a failed or cancelled save leaves nothing behind.
///  * An existing file is never replaced.
/// </summary>
public static class TrimService
{
    private const string OriginalSafe = " Your original file hasn't been changed.";

    /// <param name="outputFolder">Where to save. Null means "next to the original".</param>
    /// <param name="progress">Receives 0.0 to 1.0 while saving.</param>
    /// <param name="fileSuffix">Added to the name, e.g. "_trimmed" (the default when null or blank).</param>
    /// <returns>The full path of the saved copy.</returns>
    public static async Task<string> SaveTrimmedCopyAsync(
        MediaInfo media, TimeSpan start, TimeSpan end, string? outputFolder,
        IProgress<double>? progress, CancellationToken cancellationToken, string? fileSuffix = null)
    {
        var length = end - start;
        if (start < TimeSpan.Zero || length < TrimSelection.MinimumLength || end > media.Duration + TimeSpan.FromMilliseconds(1))
        {
            throw new FriendlyException("Please choose a longer part of the file to keep.",
                $"Invalid selection {start}..{end} for duration {media.Duration}");
        }
        if (!File.Exists(media.FilePath))
        {
            throw new FriendlyException("The original file can't be found any more. It may have been moved, renamed or deleted.",
                $"Source missing: {media.FilePath}");
        }

        var folder = outputFolder ?? Path.GetDirectoryName(Path.GetFullPath(media.FilePath))!;
        if (!Directory.Exists(folder))
        {
            throw new OutputFolderNotWritableException(folder, null, isMissing: true);
        }

        var suffix = OutputNaming.NormalizeSuffix(fileSuffix);
        var finalPath = OutputNaming.GetAvailableOutputPath(media.FilePath, folder, suffix);
        var tempPath = CreateTempFile(folder, finalPath, media.FilePath);

        Log.Info($"Saving {media.FilePath} [{start} - {end}] to {finalPath} (via {tempPath})");
        TempFileJanitor.Register(tempPath);
        var keepTemp = false;

        try
        {
            progress?.Report(0);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            LogSourceDetails(media, start, end);

            var method = await SaveByCopyingAsync(media, start, end, tempPath, progress, cancellationToken)
                ?? await SaveByReEncodingAsync(media, start, length, tempPath, progress, cancellationToken);

            await VerifyOutputAsync(tempPath, cancellationToken);
            progress?.Report(1);

            await LogResultAsync(tempPath, media, length, method, timer.Elapsed);
            finalPath = MoveIntoPlace(tempPath, media.FilePath, folder, finalPath, suffix);
            keepTemp = true; // It has become the finished file.
            Log.Info($"Saved {finalPath}");
            return finalPath;
        }
        catch (OperationCanceledException)
        {
            Log.Info("Save cancelled by the user.");
            throw;
        }
        catch (FriendlyException) { throw; }
        catch (Exception ex)
        {
            throw new FriendlyException(DescribeException(ex) + OriginalSafe, ex.ToString(), ex);
        }
        finally
        {
            TempFileJanitor.Release(tempPath, deleteFile: !keepTemp);
        }
    }

    /// <summary>
    /// Saves a video trim by copying the original data unchanged, when the selection allows it
    /// (see TrimStrategy). Returns null, after logging why, when the trim has to be re-encoded instead.
    /// </summary>
    private static async Task<string?> SaveByCopyingAsync(
        MediaInfo media, TimeSpan start, TimeSpan end, string tempPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!media.HasVideo) return null;

        CopyPlan? plan;
        string reason;
        try
        {
            (plan, reason) = await TrimStrategy.TryPlanCopyAsync(media, start, end, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            (plan, reason) = (null, $"the original couldn't be examined ({ex.Message})");
        }

        if (plan is null)
        {
            Log.Info($"Strategy: re-encode, because {reason}.");
            return null;
        }

        Log.Info($"Strategy: copy (video copied, audio copied), because {reason}. Video read limit "
            + $"{plan.VideoReadLimit?.TotalSeconds.ToString("0.000000", CultureInfo.InvariantCulture) ?? "none"} s, audio read limit "
            + $"{plan.AudioReadLimit?.TotalSeconds.ToString("0.000000", CultureInfo.InvariantCulture) ?? "none"} s, "
            + $"{plan.ExtraFramesAtEnd} extra frame(s) after the end.");

        var length = end - start;
        var result = await FFmpegRunner.RunAsync(FFmpegLocator.FFmpegPath, TrimPlanner.BuildCopyArguments(media, plan, tempPath),
            line => ReportProgress(line, length, progress), cancellationToken);

        var problem = !result.Succeeded
            ? $"ffmpeg exit code {result.ExitCode}: {result.ErrorOutput.Trim()}"
            : await CheckCopyAsync(tempPath, media, length, cancellationToken);
        if (problem is null) return "copy";

        // Copying is only a shortcut: anything unexpected and the trim is simply re-encoded.
        Log.Warn($"Copy didn't pass its checks ({problem}); re-encoding instead.");
        progress?.Report(0);
        return null;
    }

    private static async Task<string> SaveByReEncodingAsync(
        MediaInfo media, TimeSpan start, TimeSpan length, string tempPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        VideoBudget? budget = null;
        if (media.Video is { } video)
        {
            budget = await TrimStrategy.MeasureBudgetAsync(media, start, length, cancellationToken);
            Log.Info($"Video: re-encoded with {TrimPlanner.DescribeVideoEncoding(video, budget)}.");
        }
        else
        {
            Log.Info("Strategy: sound-only file, cut to the exact sample (lossless sound is rebuilt exactly).");
        }
        if (media.HasAudio) Log.Info($"Audio: re-encoded as {TrimPlanner.DescribeAudioEncoding(media)}.");

        var arguments = TrimPlanner.BuildArguments(media, start, length, tempPath, budget);
        var result = await FFmpegRunner.RunAsync(FFmpegLocator.FFmpegPath, arguments,
            line => ReportProgress(line, length, progress), cancellationToken);

        if (!result.Succeeded)
        {
            throw new FriendlyException(DescribeFailure(result.ErrorOutput) + OriginalSafe,
                $"ffmpeg exit code {result.ExitCode}: {result.ErrorOutput}");
        }
        return media.HasVideo ? "re-encode" : "audio re-encode";
    }

    /// <summary>
    /// Extra checks for a copied trim: about the right length, and the beginning and end decode
    /// without errors. Returns a description of the problem, or null if it's fine.
    /// </summary>
    private static async Task<string?> CheckCopyAsync(string path, MediaInfo media, TimeSpan length, CancellationToken cancellationToken)
    {
        var duration = await ReadDurationAsync(path, cancellationToken);
        var frame = media.Video is { FramesPerSecond: > 0 } v ? 1 / v.FramesPerSecond : 0.1;
        var tolerance = 0.1 + (TrimStrategy.MaxExtraFramesAtEnd + 1) * frame;
        if (duration is null || Math.Abs(duration.Value - length.TotalSeconds) > tolerance)
        {
            return $"length {duration?.ToString("0.000", CultureInfo.InvariantCulture) ?? "unknown"} s, expected {length.TotalSeconds:0.000} s";
        }

        foreach (var where in new[] { new[] { "-t", "3" }, new[] { "-sseof", "-3" } })
        {
            var result = await FFmpegRunner.RunAsync(FFmpegLocator.FFmpegPath,
                ["-hide_banner", "-nostdin", "-v", "error", .. where, "-i", path, "-f", "null", "-"], null, cancellationToken);
            if (!result.Succeeded || result.ErrorOutput.Trim().Length > 0)
            {
                return $"decoding {string.Join(' ', where)} reported: {result.ErrorOutput.Trim()}";
            }
        }
        return null;
    }

    private static async Task<double?> ReadDurationAsync(string path, CancellationToken cancellationToken)
    {
        string? text = null;
        var result = await FFmpegRunner.RunAsync(FFmpegLocator.FFprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path],
            line => text ??= line.Trim(), cancellationToken);
        return result.Succeeded && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Writes what the original is like and what was asked for to the diagnostic log.</summary>
    private static void LogSourceDetails(MediaInfo media, TimeSpan start, TimeSpan end)
    {
        var length = end - start;
        if (media.Video is { } v)
        {
            Log.Info($"Source video: {v.Codec} {v.Width}x{v.Height} {v.FramesPerSecond.ToString("0.###", CultureInfo.InvariantCulture)} fps, "
                + $"{FormatRate(v.BitRate)}");
        }
        foreach (var a in media.AudioStreams)
        {
            Log.Info($"Source audio: {a.Codec} {a.SampleRate} Hz {a.Channels} ch, {FormatRate(a.BitRate)}");
        }
        Log.Info($"Source file: {media.Duration.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s, {FormatSize(media.FileSize)}");

        var proportional = media.Duration > TimeSpan.Zero ? media.FileSize * (length / media.Duration) : 0;
        Log.Info($"Requested: {start.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)}-{end.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s "
            + $"({length.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s), proportional size estimate {FormatSize((long)proportional)}");
    }

    /// <summary>Writes what the saved copy turned out like (size, length, data rates) to the diagnostic log.</summary>
    private static async Task LogResultAsync(string path, MediaInfo media, TimeSpan length, string method, TimeSpan elapsed)
    {
        try
        {
            var lines = new List<string>();
            await FFmpegRunner.RunAsync(FFmpegLocator.FFprobePath,
                ["-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,bit_rate", "-of", "compact=nk=0", path],
                lines.Add, CancellationToken.None);
            var size = new FileInfo(path).Length;
            var proportional = media.Duration > TimeSpan.Zero ? media.FileSize * (length / media.Duration) : 0;
            var ratio = proportional > 0 ? (size / proportional).ToString("0.00", CultureInfo.InvariantCulture) + "x proportional" : "no estimate";
            Log.Info($"Result ({method}, {elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s): {FormatSize(size)}, {ratio}; "
                + string.Join(", ", lines.Where(l => l.Length > 0).Select(DescribeProbeLine)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not describe the result: {ex.Message}");
        }
    }

    /// <summary>Turns "stream|codec_name=h264|codec_type=video|bit_rate=5393175" into "video h264 5393 kbps".</summary>
    internal static string DescribeProbeLine(string line)
    {
        var fields = line.Split('|').Skip(1).Select(f => f.Split('=', 2)).Where(f => f.Length == 2)
            .ToDictionary(f => f[0], f => f[1]);
        if (fields.TryGetValue("duration", out var duration)) return $"length {duration} s";
        long? rate = long.TryParse(fields.GetValueOrDefault("bit_rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;
        return $"{fields.GetValueOrDefault("codec_type", "stream")} {fields.GetValueOrDefault("codec_name", "?")} {FormatRate(rate)}";
    }

    private static string FormatRate(long? bitsPerSecond) =>
        bitsPerSecond is > 0 and var b ? $"{b / 1000} kbps" : "bitrate unknown";

    private static string FormatSize(long bytes) =>
        $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes ({(bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)} MiB)";

    /// <summary>
    /// Creates the empty unfinished file. Doing this first also proves we can write to the folder,
    /// so the user hears about a read-only folder before waiting for a long save.
    /// </summary>
    private static string CreateTempFile(string folder, string finalPath, string sourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(finalPath);
        var extension = Path.GetExtension(finalPath);

        for (var attempt = 0; ; attempt++)
        {
            var suffix = attempt == 0 ? "" : "-" + Guid.NewGuid().ToString("N")[..6];
            var tempPath = Path.Combine(folder, $"{name}{TempFileJanitor.TempMarker}{suffix}{extension}");
            if (OutputNaming.IsSamePath(tempPath, sourcePath)) continue; // Paranoia: never touch the source.

            try
            {
                // CreateNew fails rather than overwrite anything that already exists.
                // (Deliberately not hidden: Windows refuses to let ffmpeg overwrite a hidden file.)
                using (new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return tempPath;
            }
            catch (IOException) when (File.Exists(tempPath) && attempt < 5)
            {
                // A leftover with this name exists; try another name.
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                throw new OutputFolderNotWritableException(folder, ex);
            }
        }
    }

    private static void ReportProgress(string line, TimeSpan length, IProgress<double>? progress)
    {
        // ffmpeg -progress prints "key=value" lines; out_time_us is how far it has written.
        if (progress is null || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
        if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us > 0)
        {
            progress.Report(Math.Clamp(us / 1_000_000.0 / length.TotalSeconds, 0, 0.99));
        }
    }

    /// <summary>Double-checks that the result is a real, readable media file before calling it done.</summary>
    private static async Task VerifyOutputAsync(string tempPath, CancellationToken cancellationToken)
    {
        var size = new FileInfo(tempPath).Length;
        if (size == 0)
        {
            throw new FriendlyException("Something went wrong while saving the trimmed copy." + OriginalSafe, "Output file is empty.");
        }

        var result = await FFmpegRunner.RunAsync(FFmpegLocator.FFprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", tempPath],
            null, cancellationToken);
        if (!result.Succeeded)
        {
            throw new FriendlyException("Something went wrong while saving the trimmed copy." + OriginalSafe,
                $"The output could not be read back: {result.ErrorOutput}");
        }
    }

    private static string MoveIntoPlace(string tempPath, string sourcePath, string folder, string finalPath, string suffix)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            // Another program may have created a file with our chosen name while we were saving.
            if (attempt > 0 || File.Exists(finalPath)) finalPath = OutputNaming.GetAvailableOutputPath(sourcePath, folder, suffix);
            if (OutputNaming.IsSamePath(finalPath, sourcePath)) continue;

            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
                return finalPath;
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Name was taken at the last moment: pick the next one.
            }
        }

        throw new FriendlyException("The trimmed file couldn't be given its final name." + OriginalSafe,
            $"Could not move {tempPath} into place in {folder}");
    }

    /// <summary>Turns ffmpeg's last words into a sentence a normal person can act on.</summary>
    internal static string DescribeFailure(string ffmpegErrors)
    {
        var e = ffmpegErrors;
        if (Has(e, "No space left on device") || Has(e, "not enough space on the disk"))
            return "The trimmed copy couldn't be saved because the disk is full. Free up some space and try again.";
        if (Has(e, "Permission denied"))
            return "The trimmed copy couldn't be saved because Paradis Trim isn't allowed to write to that folder.";
        if (Has(e, "being used by another process") || Has(e, "Device or resource busy"))
            return "The trimmed file couldn't be saved because another program is using that location.";
        if (Has(e, "Invalid data found") || Has(e, "moov atom not found") || Has(e, "Error while decoding")
            || Has(e, "corrupt") || Has(e, "partial file"))
            return "Paradis Trim couldn't read part of this file, so the trimmed copy couldn't be made. The file may be damaged.";
        return "Something went wrong while saving the trimmed copy.";
    }

    private static string DescribeException(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "The trimmed copy couldn't be saved because Paradis Trim isn't allowed to write to that folder.",
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "The trimmed file couldn't be saved because another program is using that location.",
        IOException io when (io.HResult & 0xFFFF) is 112 or 39 => "The trimmed copy couldn't be saved because the disk is full. Free up some space and try again.",
        _ => "Something went wrong while saving the trimmed copy.",
    };

    private static bool Has(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
