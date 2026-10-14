using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ParadisTrim.Core.Infrastructure;

namespace ParadisTrim.Playback;

/// <summary>
/// Preview playback. Wraps WPF's MediaElement (which uses Windows' own media playback)
/// and adds the few things Paradis Trim needs: play/pause, jumping to a time,
/// a regularly updated position, and "play up to here, then stop" for previewing an ending.
/// </summary>
public sealed class PlaybackController
{
    private readonly MediaElement _media;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _openTimeout;
    private TimeSpan? _stopAt;
    private TimeSpan _duration;

    public bool IsReady { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool HasVideo => IsReady && _media.HasVideo;
    public TimeSpan Position => _media.Position;

    /// <summary>The playhead moved (while playing, or after a jump).</summary>
    public event Action<TimeSpan>? PositionChanged;

    /// <summary>Playing started or stopped.</summary>
    public event Action<bool>? PlayingChanged;

    /// <summary>The file is ready to preview.</summary>
    public event Action? Ready;

    /// <summary>Windows can't preview this file. Trimming still works.</summary>
    public event Action? Failed;

    public PlaybackController(MediaElement media)
    {
        _media = media;
        _media.LoadedBehavior = MediaState.Manual;
        _media.UnloadedBehavior = MediaState.Manual;
        _media.ScrubbingEnabled = true; // show the right picture when jumping while paused
        _media.MediaOpened += OnMediaOpened;
        _media.MediaFailed += OnMediaFailed;
        _media.MediaEnded += OnMediaEnded;

        // About 30 updates a second keeps the playhead smooth without wasting effort.
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();

        // If Windows neither opens nor rejects a file in reasonable time, stop waiting for it.
        _openTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _openTimeout.Tick += (_, _) =>
        {
            _openTimeout.Stop();
            if (IsReady) return;
            Log.Warn("Preview did not open in time.");
            Failed?.Invoke();
        };
    }

    public void Open(string path, TimeSpan duration)
    {
        Close();
        _duration = duration;
        try
        {
            _media.Source = ToUri(path);
            // With manual control, Pause() is what actually starts loading the file.
            _media.Pause();
            _openTimeout.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"Preview could not open {path}: {ex.Message}");
            Failed?.Invoke();
        }
    }

    public void Close()
    {
        SetPlaying(false);
        _openTimeout.Stop();
        _stopAt = null;
        IsReady = false;
        try
        {
            _media.Stop();
            _media.Close();
            _media.Source = null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Closing the preview failed: {ex.Message}");
        }
    }

    public void Toggle()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Play(TimeSpan? stopAt = null)
    {
        if (!IsReady) return;
        _stopAt = stopAt;
        // Pressing play at the very end starts again from the beginning, like any player.
        if (_media.Position >= _duration - TimeSpan.FromMilliseconds(50)) Seek(TimeSpan.Zero);
        _media.Play();
        SetPlaying(true);
    }

    public void Pause()
    {
        if (!IsReady) return;
        _media.Pause();
        _stopAt = null;
        SetPlaying(false);
        PositionChanged?.Invoke(_media.Position);
    }

    public void Seek(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        if (time > _duration) time = _duration;
        if (IsReady) _media.Position = time;
        PositionChanged?.Invoke(time);
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _openTimeout.Stop();
        IsReady = true;
        _media.Position = TimeSpan.Zero; // shows the first picture of a video
        Ready?.Invoke();
        PositionChanged?.Invoke(TimeSpan.Zero);
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Log.Warn($"Windows could not preview the file: {e.ErrorException}");
        _openTimeout.Stop();
        IsReady = false;
        SetPlaying(false);
        Failed?.Invoke();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _media.Pause();
        SetPlaying(false);
        PositionChanged?.Invoke(_duration);
    }

    private void OnTick()
    {
        var position = _media.Position;
        if (_stopAt is { } stop && position >= stop)
        {
            _media.Pause();
            _media.Position = stop;
            position = stop;
            _stopAt = null;
            SetPlaying(false);
        }
        PositionChanged?.Invoke(position);
    }

    private void SetPlaying(bool playing)
    {
        if (playing) _timer.Start();
        else _timer.Stop();
        if (IsPlaying == playing) return;
        IsPlaying = playing;
        PlayingChanged?.Invoke(playing);
    }

    /// <summary>
    /// Makes a file address Windows' player understands. File names containing # or %
    /// would be misread in a plain address, so those characters are escaped.
    /// </summary>
    private static Uri ToUri(string path)
    {
        if (!path.Contains('#') && !path.Contains('%')) return new Uri(path, UriKind.Absolute);

        var full = System.IO.Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // Network path: \\server\share\folder\file
            var parts = full.TrimStart('\\').Split('\\');
            return new Uri("file://" + parts[0] + "/" + string.Join("/", parts.Skip(1).Select(Uri.EscapeDataString)));
        }

        var segments = full.Split('\\');
        return new Uri("file:///" + segments[0] + "/" + string.Join("/", segments.Skip(1).Select(Uri.EscapeDataString)));
    }
}
