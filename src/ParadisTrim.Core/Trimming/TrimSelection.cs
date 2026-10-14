namespace ParadisTrim.Core.Trimming;

/// <summary>
/// The part of the file the user wants to KEEP: everything between <see cref="Start"/> and <see cref="End"/>.
/// All the rules live here, so the window can never end up with an impossible selection
/// (start after end, past the end of the file, or a zero-length clip).
/// </summary>
public sealed class TrimSelection
{
    /// <summary>The shortest clip Paradis Trim will save.</summary>
    public static readonly TimeSpan MinimumLength = TimeSpan.FromMilliseconds(100);

    public TimeSpan Duration { get; }
    public TimeSpan Start { get; private set; }
    public TimeSpan End { get; private set; }
    public TimeSpan Length => End - Start;
    public bool IsWholeFile => Start == TimeSpan.Zero && End == Duration;

    /// <summary>Raised whenever Start or End changes.</summary>
    public event EventHandler? Changed;

    public TrimSelection(TimeSpan duration)
    {
        if (duration < MinimumLength)
        {
            throw new FriendlyException(
                "This file is too short to trim.",
                $"Duration {duration} is shorter than the minimum clip length {MinimumLength}.");
        }

        Duration = duration;
        Start = TimeSpan.Zero;
        End = duration; // The initial selection is the entire file.
    }

    /// <summary>Moves the beginning of the kept part. It can't pass the end (minus the minimum length).</summary>
    public void SetStart(TimeSpan value)
    {
        var clamped = Clamp(value, TimeSpan.Zero, End - MinimumLength);
        if (clamped == Start) return;
        Start = clamped;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the end of the kept part. It can't come before the start (plus the minimum length).</summary>
    public void SetEnd(TimeSpan value)
    {
        var clamped = Clamp(value, Start + MinimumLength, Duration);
        if (clamped == End) return;
        End = clamped;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        Start = TimeSpan.Zero;
        End = Duration;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
