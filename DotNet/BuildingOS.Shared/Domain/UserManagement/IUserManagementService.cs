namespace BuildingOS.Shared.Domain.UserManagement;

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
    /// One user's role/enabled state (as in <see cref="GetUserRoleStatesAsync"/>), so the guard can decide
    /// most operations without listing every user; <c>null</c> when the user does not exist.
    /// </summary>
    Task<UserRoleState?> GetUserRoleStateAsync(string userId, CancellationToken cancellationToken = default);

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
