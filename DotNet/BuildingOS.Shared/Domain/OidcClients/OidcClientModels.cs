namespace BuildingOS.Shared.Domain.OidcClients;

/// <summary>Row in the OIDC client list. Never carries the client secret.</summary>
public sealed record OidcClientSummary(
    string Id,
    string ClientId,
    bool Enabled,
    bool ServiceAccountsEnabled,
    string? Description);

/// <summary>OIDC client detail. Secret presence only — the value is never read back here.</summary>
public sealed record OidcClientDetail(
    string Id,
    string ClientId,
    bool Enabled,
    bool ServiceAccountsEnabled,
    bool PublicClient,
    string? Description,
    IReadOnlyList<string> RedirectUris);

/// <summary>Inputs for creating a confidential OIDC client.</summary>
public sealed record CreateOidcClientSpec(
    string ClientId,
    string? Description,
    bool ServiceAccountsEnabled,
    IReadOnlyList<string>? RedirectUris = null);

/// <summary>
/// Thrown when the OIDC client management surface is not configured (no Keycloak admin API).
/// The controller maps this to 503 so the UI can explain the configuration gap (#10) instead of 500.
/// </summary>
public sealed class OidcServiceUnavailableException : Exception
{
    public OidcServiceUnavailableException(string message) : base(message) { }
}
