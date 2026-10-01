using BuildingOS.Shared.Domain.UserManagement;

namespace BuildingOS.Shared.Domain.Authorization;

public record AuthorizationContext
{
    private readonly IReadOnlyList<string> _permissions = [];

    public required string UserId { get; init; }
    public required string Role { get; init; }

    /// <summary>
    /// The permission strings granted to the principal. Always empty for a group-manager (#506): the
    /// role manages Groups, and Group items are grants to whoever holds group:&lt;id&gt; — a holder that
    /// also carried such a grant could add any resource to its Group and use it.
    /// </summary>
    public required IReadOnlyList<string> Permissions
    {
        get => IsGroupManager ? [] : _permissions;
        init => _permissions = value;
    }

    public bool IsAdmin => Role == RoleCatalog.Admin;

    /// <summary>
    /// #506: the role an external application's service account gets to keep Building OS Groups in
    /// sync (e.g. a tenant portal) without being a full admin. Matched exactly, like <c>admin</c>.
    /// </summary>
    public bool IsGroupManager => Role == RoleCatalog.GroupManager;

    /// <summary>
    /// May create Groups, and change and delete the ones it may manage (#506) — every Group for an
    /// admin, only the ones it created for a group-manager (enforced by the Groups API).
    /// </summary>
    public bool CanManageGroups => IsAdmin || IsGroupManager;

    /// <summary>
    /// Reads the twin's STRUCTURE regardless of grants — buildings, floors, rooms, equipment, the point
    /// list and search — so a group-manager can pick the resources a Group holds (#506). A
    /// group-manager gets names and ids only (no addressing, gateways or thresholds). Never values: a
    /// single point (the gate for its control history), telemetry, data health and every write stay on
    /// grants for anyone but an admin.
    /// </summary>
    public bool ReadsWholeTwinStructure => IsAdmin || IsGroupManager;
}
