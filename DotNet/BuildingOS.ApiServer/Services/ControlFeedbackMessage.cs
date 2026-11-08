using BuildingOs.ApiServer.Protos;
using BuildingOS.Shared.Domain.ControlFeedback;

namespace BuildingOs.ApiServer.Services;

/// <summary>
/// #637: the confirmation of one control on <c>building-os.control.feedback.{controlId}</c>. <c>Status</c> is one of
/// <see cref="ControlFeedbackStatuses"/> (never pending); <c>Value</c> is the feedback value it was decided on, or
/// the reason when unavailable.
/// </summary>
public sealed record ControlFeedbackMessage(string Status, string? Value)
{
    public static ControlResult ToResult(string? status) => status switch
    {
        ControlFeedbackStatuses.Matched => ControlResult.FeedbackMatched,
        ControlFeedbackStatuses.Mismatch => ControlResult.FeedbackMismatch,
        ControlFeedbackStatuses.Timeout => ControlResult.FeedbackTimeout,
        _ => ControlResult.FeedbackUnavailable,
    };
}
