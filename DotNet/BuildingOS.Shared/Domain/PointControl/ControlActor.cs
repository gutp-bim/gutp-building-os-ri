namespace BuildingOS.Shared.Domain.PointControl;

/// <summary>
/// Who issued a control command (#461), carried from the authenticated principal at the API entry
/// point to the <c>point_control_audit</c> row.
///
/// Deliberately <b>not</b> a field on <see cref="PointControlInfo"/>: that object is JSON-serialized
/// onto NATS by <c>NatsPointControlCommandPublisher</c> and forwarded down the egress stream to the
/// gateway, so an actor field there would publish the operator's identity to every gateway. The actor
/// travels beside the command, on the audit path only.
///
/// Shaped like <c>admin_audit</c>'s actor (<c>actor_sub</c> / <c>actor_name</c>) so the two audit
/// trails can be correlated on one identifier. <see cref="Name"/> is optional and currently always
/// null — every existing <c>admin_audit</c> writer passes <c>actorName: null</c> too; when a
/// display-name source is wired, both trails get it at once.
/// </summary>
public sealed record ControlActor
{
    /// <summary>
    /// Written to <c>actor_sub</c> when no identity could be resolved. The column is NOT NULL and a
    /// blank principal is not an identity, so a row that cannot name its actor says so instead of
    /// carrying an empty string. Same sentinel <c>AuthorizationContextMiddleware</c> already uses for
    /// an unresolvable principal, so the two agree.
    /// </summary>
    public const string UnknownSub = "unknown";

    /// <summary>The principal's stable identifier (the JWT <c>sub</c> / <c>oid</c>). Never blank.</summary>
    public string Sub { get; }

    /// <summary>The principal's display name, or null when none is available.</summary>
    public string? Name { get; }

    private ControlActor(string sub, string? name)
    {
        Sub = sub;
        Name = name;
    }

    /// <summary>
    /// Normalizes a principal into an actor: trims both values, falls back to
    /// <see cref="UnknownSub"/> for a missing/blank subject, and collapses a blank name to null.
    /// The only way to build one, so no call site can bypass the normalization.
    /// </summary>
    public static ControlActor From(string? sub, string? name = null) => new(
        string.IsNullOrWhiteSpace(sub) ? UnknownSub : sub.Trim(),
        string.IsNullOrWhiteSpace(name) ? null : name.Trim());
}
