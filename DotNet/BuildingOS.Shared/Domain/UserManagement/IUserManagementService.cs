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
    /// Get a specific user by ID
    /// </summary>
    Task<EntraUser?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update user's Building OS custom security attributes (role and permissions)
    /// </summary>
    Task<EntraUser> UpdateUserAttributesAsync(
        string userId,
        UpdateUserAttributesRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enable or disable a user account (Keycloak <c>enabled</c>). Disabling blocks authentication
    /// without deleting the account (reversible); account creation/credentials stay in Keycloak.
    /// </summary>
    Task<EntraUser> SetEnabledAsync(
        string userId,
        bool enabled,
        CancellationToken cancellationToken = default);
}
