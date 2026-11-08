namespace BuildingOS.Shared.Domain.ControlFeedback;

/// <summary>A reading of the feedback point: its numeric value (null = not a number) and its own timestamp.</summary>
public readonly record struct FeedbackReading(double? Value, DateTimeOffset? At);

/// <summary>The decided result of one confirmation, and the value it was decided on (null when none arrived).</summary>
public sealed record ControlFeedbackResult(ControlFeedbackOutcome Outcome, double? ActualValue, string? Reason = null);

/// <summary>
/// The confirmation of one control as a small state machine (#637, pure — the caller owns the clock and the feed).
///
/// <list type="bullet">
/// <item>A reading timestamped before the control was sent is ignored: it is the old value arriving late, not the
/// answer to this control.</item>
/// <item>A matching reading inside the window decides <see cref="ControlFeedbackOutcome.Matched"/> at once.</item>
/// <item>A mismatch is decided only <b>at the end of the window</b>, on the last reading that arrived: devices often
/// pass through other values (ramping, an echo of the previous setpoint) before settling.</item>
/// <item>No usable reading in the window is <see cref="ControlFeedbackOutcome.Timeout"/>.</item>
/// </list>
/// </summary>
public sealed class ControlFeedbackCorrelation(ControlExpectedValue expected, DateTimeOffset sentAt)
{
    private double? _last;
    private bool _sawReading;

    public ControlFeedbackResult? Result { get; private set; }

    /// <summary>Feeds one reading. Returns the result once decided (and keeps returning it).</summary>
    public ControlFeedbackResult? Observe(FeedbackReading reading)
    {
        if (Result is not null) return Result;
        // An unknown timestamp is treated as current: the feed only carries values written after we subscribed
        // (or the snapshot, which the caller filters by its own observation time).
        if (reading.At is { } at && at < sentAt) return null;
        if (reading.Value is not { } v || !double.IsFinite(v)) return null;

        _sawReading = true;
        _last = v;
        if (expected.Matches(v)) Result = new ControlFeedbackResult(ControlFeedbackOutcome.Matched, v);
        return Result;
    }

    /// <summary>The window ended: decide on what arrived.</summary>
    public ControlFeedbackResult Finish()
    {
        if (Result is not null) return Result;
        Result = _sawReading
            ? new ControlFeedbackResult(ControlFeedbackOutcome.Mismatch, _last)
            : new ControlFeedbackResult(ControlFeedbackOutcome.Timeout, null);
        return Result;
    }
}
