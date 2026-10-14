using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ParadisTrim.Controls;
using ParadisTrim.Core;
using ParadisTrim.Core.Infrastructure;
using ParadisTrim.Core.Media;
using ParadisTrim.Core.Trimming;
using ParadisTrim.Native;
using ParadisTrim.Playback;

namespace ParadisTrim;

/// <summary>
/// The one and only main window. It moves between a few simple screens:
/// Empty (drop a file) → Loading → Editor (preview + timeline) → Saving → Done.
/// The real work happens in ParadisTrim.Core; this class connects it to the screen.
/// </summary>
public partial class MainWindow : Window
{
    private enum Screen { Empty, Loading, Editor, Done }

    /// <summary>Where a save is going, which decides what to do if that folder can't be used.</summary>
    private enum FolderKind
    {
        /// <summary>Next to the original (the default).</summary>
        Source,
        /// <summary>The folder chosen with SAVE TO → Change, remembered between launches.</summary>
        Remembered,
        /// <summary>A folder picked just for this one save, because the usual one couldn't be used.</summary>
        JustThisOnce,
    }

    private static readonly TimeSpan EndingPreviewLength = TimeSpan.FromSeconds(3);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly PlaybackController _playback;

    private MediaInfo? _media;
    private TrimSelection? _selection;
    private CancellationTokenSource? _waveformCts;
    private CancellationTokenSource? _saveCts;
    private Task? _saveTask;
    private string? _lastSavedPath;
    private bool _closingAfterSave;
    private int _openCounter;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        RestoreWindowSize();
        TaglineText.Text = AppInfo.Tagline;
        FormatsText.Text = "Works with " + SupportedFormats.FriendlyList + " files";
        UpdateSaveToDisplay();

        _playback = new PlaybackController(Player);
        _playback.PositionChanged += OnPlaybackPositionChanged;
        _playback.PlayingChanged += playing =>
        {
            PlayIconPath.Data = (Geometry)FindResource(playing ? "PauseIcon" : "PlayIcon");
            PlayIconPath.Margin = playing ? new Thickness(0) : new Thickness(3, 0, 0, 0);
            AutomationPropertiesHelper.SetName(PlayButton, playing ? "Pause" : "Play");
        };
        _playback.Ready += () => SetPreviewAvailable(true);
        _playback.Failed += () => SetPreviewAvailable(false);

        Timeline.HandleMoved += OnTimelineHandleMoved;
        Timeline.HandleReleased += OnTimelineHandleReleased;
        Timeline.SeekRequested += seconds =>
        {
            _playback.Pause();
            _playback.Seek(TimeSpan.FromSeconds(seconds));
        };

        ShowScreen(Screen.Empty);
    }

    // =====================================================================
    // Opening files
    // =====================================================================

    public void OpenFileFromCommandLine(string path) => _ = OpenFileAsync(path);

    private void OnChooseFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an audio or video file",
            Filter = SupportedFormats.OpenDialogFilter,
            CheckFileExists = true,
        };
        if (_settings.LastFolder is { } folder && Directory.Exists(folder)) dialog.InitialDirectory = folder;

        if (dialog.ShowDialog(this) == true) _ = OpenFileAsync(dialog.FileName);
    }

    private async Task OpenFileAsync(string path)
    {
        if (IsSaving) return;

        var myTurn = ++_openCounter; // if another file is opened meanwhile, this one gives way
        var previousMedia = _media;
        _playback.Close();
        _waveformCts?.Cancel();
        ShowScreen(Screen.Loading);

        MediaInfo media;
        TrimSelection selection;
        try
        {
            media = await MediaProbe.ProbeAsync(path);
            selection = new TrimSelection(media.Duration);
        }
        catch (Exception ex)
        {
            if (myTurn != _openCounter) return;
            // Go back to whatever was showing before, then explain.
            if (previousMedia is not null && File.Exists(previousMedia.FilePath))
            {
                ShowScreen(Screen.Editor);
                _playback.Open(previousMedia.FilePath, previousMedia.Duration);
            }
            else
            {
                _media = null;
                Title = AppInfo.ProductName;
                ShowScreen(Screen.Empty);
            }
            ShowError(ex);
            return;
        }

        if (myTurn != _openCounter) return;

        if (_selection is not null) _selection.Changed -= OnSelectionChanged;
        _media = media;
        _selection = selection;
        _selection.Changed += OnSelectionChanged;
        _settings.LastFolder = Path.GetDirectoryName(path);

        Title = $"{media.FileName} – {AppInfo.ProductName}";
        FileNameText.Text = media.FileName;
        FileNameText.ToolTip = media.FilePath;
        FileLengthText.Text = $"{(media.HasVideo ? "Video" : "Sound")} · {TimeFormat.Friendly(media.Duration)} long";
        AudioPlaceholder.Visibility = media.HasVideo ? Visibility.Collapsed : Visibility.Visible;
        NoPreviewMessage.Visibility = Visibility.Collapsed;

        Timeline.Duration = media.Duration.TotalSeconds;
        Timeline.Peaks = null;
        OnSelectionChanged(this, EventArgs.Empty);
        OnPlaybackPositionChanged(TimeSpan.Zero);

        SetPreviewAvailable(false, showMessage: false); // until Windows says the preview is ready
        ShowScreen(Screen.Editor);
        _playback.Open(media.FilePath, media.Duration);
        _ = LoadWaveformAsync(media);
    }

    private async Task LoadWaveformAsync(MediaInfo media)
    {
        _waveformCts?.Cancel();
        var cts = _waveformCts = new CancellationTokenSource();
        try
        {
            var peaks = await WaveformGenerator.GenerateAsync(media, 1500, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(media, _media)) Timeline.Peaks = peaks;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // The waveform is a nice extra; without it the timeline still works.
            Log.Warn($"Waveform not available: {ex.Message}");
        }
    }

    private void SetPreviewAvailable(bool available, bool showMessage = true)
    {
        PlayButton.IsEnabled = available;
        PreviewStartButton.IsEnabled = available;
        PreviewEndButton.IsEnabled = available;
        if (!available && showMessage) NoPreviewMessage.Visibility = Visibility.Visible;
        if (available) NoPreviewMessage.Visibility = Visibility.Collapsed;
    }

    // =====================================================================
    // Drag and drop
    // =====================================================================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var canDrop = !IsSaving && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        DropZoneOutline.Stroke = canDrop ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderBrush");
        DropZone.Background = canDrop ? (Brush)FindResource("AccentSoftBrush") : (Brush)FindResource("SurfaceBrush");
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void OnDrop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        e.Handled = true;
        if (IsSaving || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;

        var file = paths.FirstOrDefault(File.Exists);
        if (file is null)
        {
            MessageDialog.Inform(this, "Please drop a single audio or video file, not a folder.");
            return;
        }
        _ = OpenFileAsync(file);
    }

    private void ResetDropZone()
    {
        DropZoneOutline.Stroke = (Brush)FindResource("BorderBrush");
        DropZone.Background = (Brush)FindResource("SurfaceBrush");
    }

    // =====================================================================
    // Selection (the part to keep)
    // =====================================================================

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_selection is null) return;
        Timeline.Start = _selection.Start.TotalSeconds;
        Timeline.End = _selection.End.TotalSeconds;
        StartText.Text = TimeFormat.Friendly(_selection.Start);
        EndText.Text = TimeFormat.Friendly(_selection.End);
        LengthText.Text = TimeFormat.Friendly(_selection.Length);
    }

    private void OnTimelineHandleMoved(TimelineHandle handle, double seconds)
    {
        if (_selection is null) return;
        _playback.Pause();
        if (handle == TimelineHandle.Start) _selection.SetStart(Snap(TimeSpan.FromSeconds(seconds)));
        else _selection.SetEnd(Snap(TimeSpan.FromSeconds(seconds)));

        // Show the picture (or move the playhead) exactly where the clip now begins or ends.
        _playback.Seek(handle == TimelineHandle.Start ? _selection.Start : _selection.End);
    }

    private void OnTimelineHandleReleased(TimelineHandle handle)
    {
        if (_selection is null) return;
        _playback.Seek(handle == TimelineHandle.Start ? _selection.Start : _selection.End);
    }

    private void OnSetStartClick(object sender, RoutedEventArgs e) => _selection?.SetStart(Snap(_playback.Position));

    private void OnSetEndClick(object sender, RoutedEventArgs e) => _selection?.SetEnd(Snap(_playback.Position));

    /// <summary>
    /// Rounds a chosen time to the nearest tenth of a second, the precision the screen shows,
    /// so Start, End and "Keeping" always add up. The very end of the file is kept exact.
    /// </summary>
    private TimeSpan Snap(TimeSpan time)
    {
        if (_media is null) return time;
        if (time >= _media.Duration - TimeSpan.FromMilliseconds(50)) return _media.Duration;
        return TimeSpan.FromMilliseconds(Math.Round(time.TotalMilliseconds / 100) * 100);
    }

    // =====================================================================
    // Playback
    // =====================================================================

    private void OnPlaybackPositionChanged(TimeSpan position)
    {
        Timeline.Position = position.TotalSeconds;
        if (_media is not null)
        {
            PositionText.Text = $"{TimeFormat.Friendly(position)} / {TimeFormat.Friendly(_media.Duration)}";
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => _playback.Toggle();

    private void OnPreviewStartClick(object sender, RoutedEventArgs e)
    {
        if (_selection is null) return;
        _playback.Seek(_selection.Start);
        _playback.Play();
    }

    private void OnPreviewEndClick(object sender, RoutedEventArgs e)
    {
        if (_selection is null) return;
        var from = _selection.End - EndingPreviewLength;
        _playback.Seek(from > _selection.Start ? from : _selection.Start);
        _playback.Play(stopAt: _selection.End);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (EditorPanel.Visibility != Visibility.Visible || IsSaving || DoneOverlay.Visibility == Visibility.Visible) return;

        var small = TimeSpan.FromSeconds(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 1);
        switch (e.Key)
        {
            case Key.Space:
                // Handled here so Space never "clicks" whichever button happens to have focus.
                if (PlayButton.IsEnabled) _playback.Toggle();
                e.Handled = true;
                break;
            case Key.Left:
                _playback.Pause();
                _playback.Seek(_playback.Position - small);
                e.Handled = true;
                break;
            case Key.Right:
                _playback.Pause();
                _playback.Seek(_playback.Position + small);
                e.Handled = true;
                break;
            case Key.OemOpenBrackets:
                _selection?.SetStart(Snap(_playback.Position));
                e.Handled = true;
                break;
            case Key.OemCloseBrackets:
                _selection?.SetEnd(Snap(_playback.Position));
                e.Handled = true;
                break;
        }
    }

    // =====================================================================
    // Saving
    // =====================================================================

    private bool IsSaving => _saveTask is { IsCompleted: false };

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (IsSaving) return;
        _saveTask = _settings.UsesSourceFolder
            ? SaveAsync(null, FolderKind.Source)
            : SaveAsync(_settings.OutputFolder, FolderKind.Remembered);
    }

    private void OnCancelSaveClick(object sender, RoutedEventArgs e)
    {
        CancelSaveButton.IsEnabled = false;
        SavingText.Text = "Cancelling…";
        _saveCts?.Cancel();
    }

    private async Task SaveAsync(string? outputFolder, FolderKind kind)
    {
        if (_media is null || _selection is null) return;

        _playback.Pause();
        _saveCts = new CancellationTokenSource();
        ShowSaving(true);

        var progress = new Progress<double>(fraction =>
        {
            if (_saveCts is { IsCancellationRequested: true }) return;
            var percent = (int)Math.Round(fraction * 100);
            SavingProgress.Value = percent;
            SavingText.Text = $"Saving trimmed copy… {percent}%";
        });

        OutputFolderNotWritableException? folderProblem = null;
        string? savedPath = null;
        try
        {
            savedPath = await TrimService.SaveTrimmedCopyAsync(
                _media, _selection.Start, _selection.End, outputFolder, progress, _saveCts.Token, _settings.FileSuffix);
            _lastSavedPath = savedPath;
        }
        catch (OperationCanceledException)
        {
            // The user chose to stop. The unfinished copy has already been removed.
        }
        catch (OutputFolderNotWritableException ex)
        {
            Log.Warn(ex.TechnicalDetails);
            folderProblem = ex;
        }
        catch (Exception ex)
        {
            if (!_closingAfterSave) ShowError(ex);
            else Log.Error("Save failed while closing", ex);
        }
        finally
        {
            ShowSaving(false);
            _saveCts.Dispose();
            _saveCts = null;
        }

        if (_closingAfterSave) return;

        if (savedPath is not null)
        {
            ShowDone(savedPath);
            if (_settings.OpenFolderAfterSave)
            {
                Log.Info("Opening the output folder after saving (Settings: on).");
                RevealInExplorer(savedPath);
            }
        }
        else if (folderProblem is not null)
        {
            var next = AskForAnotherFolder(folderProblem, kind);
            if (next is { } choice) _saveTask = SaveAsync(choice.Folder, choice.Kind);
        }
    }

    /// <summary>
    /// The folder for this save couldn't be used. Explains why and offers a way forward.
    /// Returns where to try again, or null if the user would rather stop.
    /// </summary>
    private (string? Folder, FolderKind Kind)? AskForAnotherFolder(OutputFolderNotWritableException problem, FolderKind kind)
    {
        if (kind == FolderKind.Remembered)
        {
            // The SAVE TO folder has gone (unplugged drive, deleted folder) or became read-only.
            var message = problem.IsMissing
                ? "The SAVE TO folder can't be found."
                : "Paradis Trim isn't allowed to save in the SAVE TO folder.";
            var detail = problem.Folder + "\n\n" + (problem.IsMissing
                ? "It may have been moved or deleted, or be on a drive that isn't connected. Choose another folder, or save next to the original file instead."
                : "Choose another folder, or save next to the original file instead.");
            var answer = MessageDialog.Show(this, message, detail, ["Choose Folder…", "Use Source Folder", "Cancel"], 0, 2);
            if (answer == 0)
            {
                var folder = PickFolder(null);
                if (folder is null) return null;
                RememberOutputFolder(folder);
                return (folder, FolderKind.Remembered);
            }
            if (answer == 1)
            {
                RememberOutputFolder(null);
                return (null, FolderKind.Source);
            }
            return null;
        }

        // The folder next to the original (or a folder picked just now) can't be written to,
        // e.g. the original is on a read-only drive. Ask for a folder for this one save.
        var text = kind == FolderKind.Source
            ? "Paradis Trim isn't allowed to save in the folder where the original file is."
            : "Paradis Trim can't save in that folder either.";
        var choice = MessageDialog.Show(this, text, "Please choose another folder for the trimmed copy. Your original file hasn't been changed.",
            ["Choose Folder…", "Cancel"], 0, 1);
        if (choice != 0) return null;
        var picked = PickFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        return picked is null ? null : (picked, FolderKind.JustThisOnce);
    }
    private void ShowSaving(bool saving)
    {
        SavingOverlay.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        EditorContent.IsEnabled = !saving;
        OpenAnotherButton.IsEnabled = !saving;
        SettingsButton.IsEnabled = !saving;
        CancelSaveButton.IsEnabled = true;
        SavingProgress.Value = 0;
        SavingText.Text = "Saving trimmed copy… 0%";
        if (saving) CancelSaveButton.Focus();
    }

    private void ShowDone(string outputPath)
    {
        SavedNameText.Text = Path.GetFileName(outputPath);
        SavedFolderText.Text = "in " + Path.GetDirectoryName(outputPath);
        ShowScreen(Screen.Done);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_lastSavedPath is not null) RevealInExplorer(_lastSavedPath);
    }

    /// <summary>Opens File Explorer at the saved file's folder, with the new file already selected.</summary>
    private static void RevealInExplorer(string savedPath)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{savedPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open folder: {ex.Message}");
        }
    }

    // =====================================================================
    // SAVE TO (where trimmed copies go) and Settings
    // =====================================================================

    private void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        var start = !_settings.UsesSourceFolder && Directory.Exists(_settings.OutputFolder)
            ? _settings.OutputFolder
            : _media is not null ? Path.GetDirectoryName(_media.FilePath) : null;
        if (PickFolder(start) is { } folder) RememberOutputFolder(folder);
    }

    private void OnUseSourceFolderClick(object sender, RoutedEventArgs e) => RememberOutputFolder(null);

    /// <summary>Shows Windows' folder picker. Returns the chosen folder, or null if cancelled.</summary>
    private string? PickFolder(string? startFolder)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save trimmed copies" };
        if (startFolder is not null && Directory.Exists(startFolder)) dialog.InitialDirectory = startFolder;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    /// <summary>Sets the SAVE TO folder (null = same folder as source) and remembers it right away.</summary>
    private void RememberOutputFolder(string? folder)
    {
        _settings.OutputFolder = folder;
        _settings.Save();
        Log.Info(folder is null ? "Save to: same folder as source" : $"Save to: {folder}");
        UpdateSaveToDisplay();
    }

    private void UpdateSaveToDisplay()
    {
        if (_settings.UsesSourceFolder)
        {
            SaveToText.Text = "Same folder as source";
            SaveToText.ToolTip = _media is not null ? Path.GetDirectoryName(_media.FilePath) : "Trimmed copies are saved next to the original file";
            UseSourceFolderButton.Visibility = Visibility.Collapsed;
            SaveToProblemText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SaveToText.Text = _settings.OutputFolder;
            SaveToText.ToolTip = _settings.OutputFolder;
            UseSourceFolderButton.Visibility = Visibility.Visible;
            SaveToProblemText.Visibility = Directory.Exists(_settings.OutputFolder) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _playback.Pause();
        new SettingsWindow(_settings) { Owner = this }.ShowDialog();
    }

    private void OnTrimAnotherClick(object sender, RoutedEventArgs e)
    {
        _playback.Close();
        _waveformCts?.Cancel();
        if (_selection is not null) _selection.Changed -= OnSelectionChanged;
        _media = null;
        _selection = null;
        Title = AppInfo.ProductName;
        ShowScreen(Screen.Empty);
    }

    private void OnBackToFileClick(object sender, RoutedEventArgs e) => ShowScreen(Screen.Editor);

    // =====================================================================
    // Screens, errors, About, closing
    // =====================================================================

    private void ShowScreen(Screen screen)
    {
        EmptyPanel.Visibility = screen == Screen.Empty ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = screen == Screen.Loading ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.Visibility = screen is Screen.Editor or Screen.Done ? Visibility.Visible : Visibility.Collapsed;
        DoneOverlay.Visibility = screen == Screen.Done ? Visibility.Visible : Visibility.Collapsed;
        FileHeader.Visibility = screen is Screen.Editor or Screen.Done ? Visibility.Visible : Visibility.Collapsed;
        if (screen == Screen.Editor) UpdateSaveToDisplay();
        OpenAnotherButton.Visibility = screen is Screen.Editor or Screen.Done ? Visibility.Visible : Visibility.Collapsed;
        if (screen == Screen.Empty) ResetDropZone();
    }

    private void ShowError(Exception ex)
    {
        string message;
        if (ex is FriendlyException friendly)
        {
            Log.Error(friendly.TechnicalDetails);
            message = friendly.Message;
        }
        else
        {
            Log.Error("Unexpected error", ex);
            message = "Something unexpected went wrong. Your original file hasn't been changed.";
        }
        MessageDialog.Inform(this, message);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        _playback.Pause();
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsSaving)
        {
            e.Cancel = true;
            if (_closingAfterSave) return;

            var answer = MessageDialog.Show(this, "Paradis Trim is still saving your trimmed copy.",
                "Stop saving and close? The unfinished copy will be removed. Your original file is not affected.",
                ["Keep Saving", "Stop and Close"], 0, 0);
            if (answer != 1) return;

            _closingAfterSave = true;
            _ = StopSavingThenCloseAsync();
            return;
        }

        _waveformCts?.Cancel();
        _playback.Close();
        SaveWindowSize();
        _settings.Save();
    }

    private async Task StopSavingThenCloseAsync()
    {
        _saveCts?.Cancel();
        try
        {
            if (_saveTask is not null) await _saveTask;
        }
        catch (Exception ex)
        {
            Log.Warn($"Save ended with an error while closing: {ex.Message}");
        }
        Close();
    }

    private void RestoreWindowSize()
    {
        if (_settings.WindowWidth >= MinWidth && _settings.WindowHeight >= MinHeight)
        {
            Width = Math.Min(_settings.WindowWidth, SystemParameters.WorkArea.Width);
            Height = Math.Min(_settings.WindowHeight, SystemParameters.WorkArea.Height);
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowSize()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
    }
}

/// <summary>Small helper so screen readers announce the play button's current action.</summary>
internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
