using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ParadisTrim.Controls;

/// <summary>Which part of the timeline the user is dragging.</summary>
public enum TimelineHandle { Start, End }

/// <summary>
/// The trimming timeline: a waveform (when there is sound), the KEPT section highlighted
/// between two draggable handles, the parts that will be cut away dimmed, and a playhead.
///
/// The control only draws and reports what the mouse does; the window decides what the
/// new start/end are (via TrimSelection, which enforces the rules) and sets them back.
/// All times are in seconds.
/// </summary>
public sealed class TrimTimeline : FrameworkElement
{
    private const double HandleWidth = 14;
    private const double HandleGrabDistance = 12;
    private const double TrackInset = HandleWidth / 2 + 1;

    // Colours match the Paradis EZ dark theme in App.xaml (the blue is AccentColor).
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x30)));
    private static readonly Brush KeptBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x33, 0x55)));
    private static readonly Brush KeptWaveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6F, 0xA4, 0xFF)));
    private static readonly Brush CutWaveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0x53, 0x5A)));
    private static readonly Brush HandleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)));
    private static readonly Brush HandleActiveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0xAB, 0xFF)));
    private static readonly Pen KeptOutline = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)), 2));
    private static readonly Pen GripPen = Freeze(new Pen(Brushes.White, 1.5));
    private static readonly Pen PlayheadPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5)), 2));
    private static readonly Pen CenterLinePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x4E, 0x55)), 1));

    private TimelineHandle? _draggingHandle;
    private bool _scrubbing;
    private TimelineHandle? _hoverHandle;

    // ---- Values the window sets ----

    public static readonly DependencyProperty DurationProperty = Register(nameof(Duration), 1.0);
    public static readonly DependencyProperty StartProperty = Register(nameof(Start), 0.0);
    public static readonly DependencyProperty EndProperty = Register(nameof(End), 1.0);
    public static readonly DependencyProperty PositionProperty = Register(nameof(Position), 0.0);
    public static readonly DependencyProperty PeaksProperty = Register<float[]?>(nameof(Peaks), null);

    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Start { get => (double)GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double End { get => (double)GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    /// <summary>Waveform loudness values (0..1) spread over the whole file, or null while not available.</summary>
    public float[]? Peaks { get => (float[]?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }

    // ---- What the user does ----

    /// <summary>A handle is being dragged to a new time.</summary>
    public event Action<TimelineHandle, double>? HandleMoved;

    /// <summary>The user let go of a handle.</summary>
    public event Action<TimelineHandle>? HandleReleased;

    /// <summary>The user clicked or dragged on the timeline to move the playhead.</summary>
    public event Action<double>? SeekRequested;

    public TimelineHandle? ActiveHandle => _draggingHandle;

    public TrimTimeline()
    {
        Focusable = false;
        Cursor = Cursors.Hand;
        ToolTip = "Drag the blue handles to choose the part to keep. Click anywhere to jump there.";
        SnapsToDevicePixels = true;
    }

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= TrackInset * 2 || height <= 0) return;

        // Transparent background so the whole area reacts to the mouse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var track = new Rect(TrackInset, 6, width - TrackInset * 2, height - 12);
        var startX = TimeToX(Start);
        var endX = TimeToX(End);
        var kept = new Rect(startX, track.Top, Math.Max(0, endX - startX), track.Height);

        dc.DrawRoundedRectangle(TrackBrush, null, track, 6, 6);
        dc.DrawRectangle(KeptBrush, null, kept);
        DrawWaveform(dc, track, startX, endX);
        dc.DrawRectangle(null, KeptOutline, kept);

        DrawHandle(dc, startX, track, TimelineHandle.Start);
        DrawHandle(dc, endX, track, TimelineHandle.End);

        // Playhead: a light line with a small marker on top.
        var px = Math.Round(TimeToX(Position)) + 0.5;
        dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, height));
        dc.DrawEllipse(PlayheadPen.Brush, null, new Point(px, 3), 4, 4);
    }

    private void DrawWaveform(DrawingContext dc, Rect track, double startX, double endX)
    {
        var mid = track.Top + track.Height / 2;
        var peaks = Peaks;
        if (peaks is null || peaks.Length == 0)
        {
            dc.DrawLine(CenterLinePen, new Point(track.Left + 4, mid), new Point(track.Right - 4, mid));
            return;
        }

        var maxHalf = track.Height / 2 - 5;
        var keptGeometry = new StreamGeometry();
        var cutGeometry = new StreamGeometry();
        using (var kept = keptGeometry.Open())
        using (var cut = cutGeometry.Open())
        {
            // One thin bar per 3 pixels: readable, and quick to draw.
            for (var x = track.Left + 2; x < track.Right - 2; x += 3)
            {
                var from = (int)((x - track.Left) / track.Width * peaks.Length);
                var to = Math.Max(from + 1, (int)((x + 3 - track.Left) / track.Width * peaks.Length));
                float peak = 0;
                for (var i = from; i < to && i < peaks.Length; i++) peak = Math.Max(peak, peaks[i]);

                var half = Math.Max(1, peak * maxHalf);
                var target = x >= startX && x <= endX ? kept : cut;
                target.BeginFigure(new Point(x, mid - half), true, true);
                target.LineTo(new Point(x + 2, mid - half), false, false);
                target.LineTo(new Point(x + 2, mid + half), false, false);
                target.LineTo(new Point(x, mid + half), false, false);
            }
        }
        keptGeometry.Freeze();
        cutGeometry.Freeze();
        dc.DrawGeometry(CutWaveBrush, null, cutGeometry);
        dc.DrawGeometry(KeptWaveBrush, null, keptGeometry);
    }

    private void DrawHandle(DrawingContext dc, double x, Rect track, TimelineHandle which)
    {
        var active = _draggingHandle == which || _hoverHandle == which;
        var rect = new Rect(x - HandleWidth / 2, track.Top - 4, HandleWidth, track.Height + 8);
        dc.DrawRoundedRectangle(active ? HandleActiveBrush : HandleBrush, null, rect, 4, 4);

        // Two small grip lines say "you can drag this".
        var mid = rect.Top + rect.Height / 2;
        for (var dx = -2.5; dx <= 2.5; dx += 5)
        {
            dc.DrawLine(GripPen, new Point(x + dx, mid - 7), new Point(x + dx, mid + 7));
        }
    }

    // ---- Mouse ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var x = e.GetPosition(this).X;
        _draggingHandle = HitTestHandle(x);
        _scrubbing = _draggingHandle is null;
        CaptureMouse();

        if (_scrubbing) SeekRequested?.Invoke(XToTime(x));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var x = e.GetPosition(this).X;

        if (_draggingHandle is { } handle)
        {
            HandleMoved?.Invoke(handle, XToTime(x));
        }
        else if (_scrubbing)
        {
            SeekRequested?.Invoke(XToTime(x));
        }
        else
        {
            var hover = HitTestHandle(x);
            if (hover != _hoverHandle)
            {
                _hoverHandle = hover;
                Cursor = hover is null ? Cursors.Hand : Cursors.SizeWE;
                InvalidateVisual();
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        FinishDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishDrag();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverHandle is not null)
        {
            _hoverHandle = null;
            InvalidateVisual();
        }
    }

    private void FinishDrag()
    {
        var released = _draggingHandle;
        _draggingHandle = null;
        _scrubbing = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (released is { } handle) HandleReleased?.Invoke(handle);
        InvalidateVisual();
    }

    /// <summary>Finds the handle under the mouse; when they overlap, the side the mouse is on wins.</summary>
    private TimelineHandle? HitTestHandle(double x)
    {
        var startX = TimeToX(Start);
        var endX = TimeToX(End);
        var nearStart = Math.Abs(x - startX) <= HandleGrabDistance;
        var nearEnd = Math.Abs(x - endX) <= HandleGrabDistance;
        if (nearStart && nearEnd) return x > (startX + endX) / 2 ? TimelineHandle.End : TimelineHandle.Start;
        if (nearStart) return TimelineHandle.Start;
        if (nearEnd) return TimelineHandle.End;
        return null;
    }

    // ---- Time <-> pixels ----

    private double TrackWidth => Math.Max(1, ActualWidth - TrackInset * 2);

    private double TimeToX(double seconds) =>
        TrackInset + Math.Clamp(Duration > 0 ? seconds / Duration : 0, 0, 1) * TrackWidth;

    private double XToTime(double x) =>
        Math.Clamp((x - TrackInset) / TrackWidth, 0, 1) * Duration;

    // ---- Helpers ----

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(TrimTimeline),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
