namespace BuildingOS.Shared.Domain.UserManagement;

/// <summary>
/// Thrown when the user management surface is not configured (no Keycloak admin API).
/// The controller maps this to 503 so the UI can explain the configuration gap instead of the
/// request failing DI activation with a 500 (#293). Mirrors the OIDC client-management counterpart.
/// </summary>
public sealed class UserManagementUnavailableException : Exception
{
    public UserManagementUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Registered when the Keycloak admin API is not configured. Every operation throws
/// <see cref="UserManagementUnavailableException"/>, so <c>UsersController</c> can be activated and
/// answer 503 rather than failing DI resolution with an unhandled 500 (#293).
/// </summary>
public sealed class UnconfiguredUserManagementService : IUserManagementService
{
    private const string Message =
        "User management is not configured (KEYCLOAK_AUTHORITY / KEYCLOAK_ADMIN_CLIENT_ID / KEYCLOAK_REALM).";

    public Task<IReadOnlyList<EntraUser>> GetUsersAsync(CancellationToken cancellationToken = default) => throw New();

    public Task<EntraUser?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default) => throw New();

    public Task<EntraUser> UpdateUserAttributesAsync(
        string userId,
        UpdateUserAttributesRequest request,
        CancellationToken cancellationToken = default) => throw New();

    public Task<EntraUser> SetEnabledAsync(
        string userId,
        bool enabled,
        CancellationToken cancellationToken = default) => throw New();

    private static UserManagementUnavailableException New() => new(Message);
}
