namespace BuildingOS.Shared.Domain.PointControl;

public class PointControlAuditEntry
{
    public Guid Id { get; set; }
    public string? PointId { get; set; }
    public string Request { get; set; } = "";
    public string? Result { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Who issued the control (#461). Same column name/type as <c>admin_audit.actor_sub</c>, so the
    /// two audit trails join on one identifier. Set from <see cref="ControlActor"/>, never blank in a
    /// persisted row — see <see cref="ControlActor.UnknownSub"/>.
    /// </summary>
    public string ActorSub { get; set; } = "";

    /// <summary>
    /// The actor's display name, or null when none is available — as in <c>admin_audit.actor_name</c>,
    /// which every current writer also leaves null.
    /// </summary>
    public string? ActorName { get; set; }

    /// <summary>
    /// #637: whether the control took effect — <c>pending</c> while it is being confirmed, then <c>matched</c> /
    /// <c>mismatch</c> / <c>timeout</c> / <c>unavailable</c> (<see cref="ControlFeedback.ControlFeedbackStatuses"/>).
    /// Null for a control that was never confirmed (it failed, or predates #637).
    /// </summary>
    public string? FeedbackStatus { get; set; }

    /// <summary>#637: the feedback point's value the outcome was decided on (invariant text), or the reason when unavailable.</summary>
    public string? FeedbackValue { get; set; }

    public DateTime? FeedbackCompletedAt { get; set; }
}
