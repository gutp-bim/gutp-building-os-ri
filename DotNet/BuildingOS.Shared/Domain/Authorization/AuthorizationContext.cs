namespace BuildingOS.Shared.Domain.Authorization;

public record AuthorizationContext
{
    public required string UserId { get; init; }
    public required string Role { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }

    public bool IsAdmin => Role == "admin";

    /// <summary>
    /// #506: the role an external application's service account gets to keep Building OS Groups in
    /// sync (e.g. a tenant portal) without being a full admin. Matched exactly, like <c>admin</c>.
    /// </summary>
    public bool IsGroupManager => Role == "group-manager";

    /// <summary>May create, change and delete Groups and their resource items (#506).</summary>
    public bool CanManageGroups => IsAdmin || IsGroupManager;

    /// <summary>
    /// Reads the twin's STRUCTURE in full regardless of grants — buildings, floors, rooms, equipment,
    /// the point list and search — so a group-manager can pick the resources a Group holds (#506).
    /// Never values: a single point (the gate for its control history), telemetry, data health and
    /// every write stay on grants for anyone but an admin.
    /// </summary>
    public bool ReadsWholeTwinStructure => IsAdmin || IsGroupManager;
}
