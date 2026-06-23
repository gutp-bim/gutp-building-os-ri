namespace BuildingOS.Shared.Domain.OidcClients;

/// <summary>
/// Manages Keycloak OIDC clients (confidential / service-account apps) via the Keycloak admin API.
/// Creating/rotating returns the generated secret <b>once</b>; list/get never expose secrets.
/// Throws <see cref="OidcServiceUnavailableException"/> when the admin API is not configured.
/// </summary>
public interface IOidcClientManagementService
{
    Task<IReadOnlyList<OidcClientSummary>> ListClientsAsync(CancellationToken ct = default);

    Task<OidcClientDetail?> GetClientAsync(string id, CancellationToken ct = default);

    /// <summary>Create a confidential client. Returns the detail plus the one-time plaintext secret.</summary>
    Task<(OidcClientDetail Client, string Secret)> CreateClientAsync(
        CreateOidcClientSpec spec, CancellationToken ct = default);

    /// <summary>Regenerate the client secret. Returns the new one-time plaintext secret.</summary>
    Task<string> RotateSecretAsync(string id, CancellationToken ct = default);

    Task<OidcClientDetail> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default);

    Task DeleteClientAsync(string id, CancellationToken ct = default);
}
