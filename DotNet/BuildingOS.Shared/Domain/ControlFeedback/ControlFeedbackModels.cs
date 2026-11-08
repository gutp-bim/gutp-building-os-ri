namespace BuildingOS.Shared.Domain.ControlFeedback;

/// <summary>
/// How a control on a point is confirmed (#637), from the twin: <c>bos:feedbackPoint</c> names the point whose
/// value shows whether the write took effect (the controlled point itself when it reports its own present value).
/// </summary>
/// <param name="FeedbackPointId">Business id of the point to watch; null = this point declares no feedback.</param>
/// <param name="Window">How long to watch (<c>bos:feedbackWindowSec</c>, default <see cref="DefaultWindow"/>).</param>
/// <param name="Tolerance">Allowed |actual − expected| for a number (<c>bos:feedbackTolerance</c>, default 0).</param>
public sealed record ControlFeedbackConfig(string? FeedbackPointId, TimeSpan Window, double Tolerance)
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);

    /// <summary>Longest window honoured, so a typo in the twin cannot keep a confirmation open for hours.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(300);

    public static readonly ControlFeedbackConfig None = new(null, DefaultWindow, 0);

    public static TimeSpan ClampWindow(double? seconds) =>
        seconds is { } s && double.IsFinite(s) && s > 0
            ? TimeSpan.FromSeconds(Math.Min(s, MaxWindow.TotalSeconds))
            : DefaultWindow;
}

/// <summary>The outcome of confirming a control (#637). Wire / audit spelling in <see cref="ControlFeedbackStatuses"/>.</summary>
public enum ControlFeedbackOutcome
{
    /// <summary>A value equal to the expected one arrived within the window.</summary>
    Matched,

    /// <summary>At the end of the window the latest value that arrived differs from the expected one.</summary>
    Mismatch,

    /// <summary>No value of the feedback point arrived within the window.</summary>
    Timeout,

    /// <summary>Cannot be confirmed: no feedback point, no expected value, or the confirmation was interrupted.</summary>
    Unavailable,
}

public static class ControlFeedbackStatuses
{
    /// <summary>Confirmation is running (audit only; never sent to a client).</summary>
    public const string Pending = "pending";

    public const string Matched = "matched";
    public const string Mismatch = "mismatch";
    public const string Timeout = "timeout";
    public const string Unavailable = "unavailable";

    public static string Of(ControlFeedbackOutcome o) => o switch
    {
        ControlFeedbackOutcome.Matched => Matched,
        ControlFeedbackOutcome.Mismatch => Mismatch,
        ControlFeedbackOutcome.Timeout => Timeout,
        _ => Unavailable,
    };
}

/// <summary>Why a control cannot be confirmed (the detail of <see cref="ControlFeedbackOutcome.Unavailable"/>).</summary>
public static class ControlFeedbackUnavailableReasons
{
    public const string NoFeedbackPoint = "no_feedback_point";
    public const string ReleasePoint = "release_point";
    public const string NoExpectedValue = "no_expected_value";
    public const string Interrupted = "interrupted";
}
