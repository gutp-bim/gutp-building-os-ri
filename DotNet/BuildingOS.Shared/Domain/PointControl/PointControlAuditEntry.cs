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
}
