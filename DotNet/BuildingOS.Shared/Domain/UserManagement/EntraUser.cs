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
}

/// <summary>
/// Request to update user's Building OS attributes
/// </summary>
public class UpdateUserAttributesRequest
{
    public string? Role { get; init; }
    public IReadOnlyList<string>? Permissions { get; init; }
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
