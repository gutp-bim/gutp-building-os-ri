namespace BuildingOS.Shared.Domain.OidcClients;

/// <summary>
/// Registered when the Keycloak admin API is not configured. Every operation throws
/// <see cref="OidcServiceUnavailableException"/> so the controller returns 503 (not 500) and the UI
/// can explain the configuration gap (#10).
/// </summary>
public sealed class UnconfiguredOidcClientService : IOidcClientManagementService
{
    private const string Message =
        "OIDC client management is not configured (KEYCLOAK_AUTHORITY / KEYCLOAK_ADMIN_CLIENT_ID / KEYCLOAK_REALM).";

    public Task<IReadOnlyList<OidcClientSummary>> ListClientsAsync(CancellationToken ct = default) => throw New();

    public Task<OidcClientDetail?> GetClientAsync(string id, CancellationToken ct = default) => throw New();

    public Task<(OidcClientDetail Client, string Secret)> CreateClientAsync(
        CreateOidcClientSpec spec, CancellationToken ct = default) => throw New();

    public Task<string> RotateSecretAsync(string id, CancellationToken ct = default) => throw New();

    public Task<OidcClientDetail> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default) => throw New();

    public Task DeleteClientAsync(string id, CancellationToken ct = default) => throw New();

    private static OidcServiceUnavailableException New() => new(Message);
}
