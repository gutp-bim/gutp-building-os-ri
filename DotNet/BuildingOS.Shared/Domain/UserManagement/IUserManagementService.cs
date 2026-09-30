namespace BuildingOS.Shared.Domain.UserManagement;

/// <summary>
/// Role states resolved for one lockout-guard evaluation (<see cref="UserAdminGuard"/>), sharing one admin
/// token and group cache, so resolving the target and then the full snapshot does not repeat work.
/// </summary>
public interface IUserRoleLookup
{
    /// <summary>
    /// One user's state; <c>null</c> when the user does not exist. <paramref name="includeGroupRole"/>
    /// resolves <see cref="UserRoleState.GroupRole"/> even when the user's own role hides it (needed when
    /// the own role is about to be cleared).
    /// </summary>
    Task<UserRoleState?> GetUserAsync(string userId, bool includeGroupRole, CancellationToken cancellationToken = default);

    /// <summary>Every user's state (as <see cref="IUserManagementService.GetUserRoleStatesAsync"/>).</summary>
    Task<IReadOnlyList<UserRoleState>> GetAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Service for managing Azure Entra ID users and their Building OS attributes
/// </summary>
public interface IUserManagementService
{
    /// <summary>
    /// Get all users from Azure Entra ID
    /// </summary>
    Task<IReadOnlyList<EntraUser>> GetUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get every user's role/enabled state for the lockout guard (<see cref="UserAdminGuard"/>),
    /// including the role inherited from Keycloak groups (<see cref="UserRoleState.GroupRole"/>), so a
    /// group-derived admin counts as an admin.
    /// </summary>
    Task<IReadOnlyList<UserRoleState>> GetUserRoleStatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a role lookup for one guard evaluation: one admin token and one group cache shared by the
    /// target lookup and (only if needed) the full snapshot.
    /// </summary>
    Task<IUserRoleLookup> CreateRoleLookupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific user by ID. <see cref="EntraUser.Role"/> is the role that reaches authorization:
    /// own <c>role</c>, else the group-derived role (fail-closed when groups disagree), else the legacy
    /// <c>buildingos_role</c>.
    /// </summary>
    Task<EntraUser?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update user's Building OS attributes (role and permissions) in one read-modify-write, and verify
    /// Keycloak stored them.
    /// </summary>
    /// <exception cref="UserNotFoundException">The user does not exist.</exception>
    /// <exception cref="UserAttributesNotPersistedException">Keycloak accepted the write but did not store it.</exception>
    Task<EntraUser> UpdateUserAttributesAsync(
        string userId,
        UpdateUserAttributesRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enable or disable a user account (Keycloak <c>enabled</c>). Disabling blocks authentication
    /// without deleting the account (reversible); account creation/credentials stay in Keycloak.
    /// </summary>
    /// <exception cref="UserNotFoundException">The user does not exist.</exception>
    Task<EntraUser> SetEnabledAsync(
        string userId,
        bool enabled,
        CancellationToken cancellationToken = default);
}
