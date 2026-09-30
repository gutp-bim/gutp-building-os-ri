namespace BuildingOS.Shared.Domain.UserManagement;

/// <summary>
/// Azure Entra ID user representation
/// </summary>
public class EntraUser
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public string? UserPrincipalName { get; init; }
    public string? Role { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>Whether the account can authenticate (Keycloak <c>enabled</c>). Defaults to true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The user has no own <c>role</c>, so their Keycloak groups decide it, and the groups could not be read
    /// (e.g. the admin service account lacks <c>query-groups</c>, or a transient 5xx) — #532. The role that
    /// reaches the token is unknown, so <see cref="Role"/> is <c>null</c> rather than a guess.
    /// </summary>
    public bool GroupRoleUnresolved { get; init; }

    /// <summary>
    /// The user's own <c>role</c> attribute, ignoring groups and the legacy <c>buildingos_role</c> (which a
    /// group role would hide, so it can never stand in for an unknown group role — #532 review). The
    /// Admin-API fallback authorizes from it when <see cref="GroupRoleUnresolved"/>; when it is empty the
    /// fallback fails closed (<c>role=user</c>, no permissions).
    /// </summary>
    public string? OwnAttributeRole { get; init; }
}

/// <summary>
/// Request to update user's Building OS attributes
/// </summary>
public class UpdateUserAttributesRequest
{
    /// <summary>
    /// The new role, already normalized by <see cref="KeycloakUserAttributes.NormalizeRole"/>: <c>null</c>
    /// leaves the stored role attributes untouched, <c>""</c> clears the role.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>The full permission set; <c>null</c> keeps the current (merged) set.</summary>
    public IReadOnlyList<string>? Permissions { get; init; }

    /// <summary>Permissions added to the set, applied in the same read-modify-write (no separate read).</summary>
    public IReadOnlyList<string>? PermissionsToAdd { get; init; }

    /// <summary>Permissions removed from the set, applied in the same read-modify-write.</summary>
    public IReadOnlyList<string>? PermissionsToRemove { get; init; }
}

/// <summary>
/// Debug info extracted from access token
/// </summary>
public class TokenDebugInfo
{
    public string? AppId { get; init; }
    public string? TenantId { get; init; }
    public string? Audience { get; init; }
    public List<string> Roles { get; init; } = [];
    public string? Error { get; init; }
}
