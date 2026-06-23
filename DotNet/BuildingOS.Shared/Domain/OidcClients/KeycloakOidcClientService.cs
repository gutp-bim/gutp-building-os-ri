using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Domain.OidcClients;

/// <summary>
/// <see cref="IOidcClientManagementService"/> backed by the Keycloak admin REST API
/// (<c>/admin/realms/{realm}/clients</c>). Mirrors the admin-token pattern used by
/// <c>KeycloakUserManagementService</c>.
/// </summary>
public sealed class KeycloakOidcClientService : IOidcClientManagementService
{
    private readonly HttpClient _httpClient;
    private readonly string _realm;
    private readonly string _adminClientId;
    private readonly string _adminClientSecret;
    private readonly ILogger<KeycloakOidcClientService> _logger;

    public KeycloakOidcClientService(
        HttpClient httpClient,
        string realm,
        string adminClientId,
        string adminClientSecret,
        ILogger<KeycloakOidcClientService> logger)
    {
        _httpClient = httpClient;
        _realm = realm;
        _adminClientId = adminClientId;
        _adminClientSecret = adminClientSecret;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OidcClientSummary>> ListClientsAsync(CancellationToken ct = default)
    {
        var token = await GetAdminTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/realms/{_realm}/clients");
        request.Headers.Authorization = Bearer(token);

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var clients = await response.Content
            .ReadFromJsonAsync<ClientRepresentation[]>(cancellationToken: ct)
            .ConfigureAwait(false);
        return clients?.Select(ToSummary).ToList() ?? [];
    }

    public async Task<OidcClientDetail?> GetClientAsync(string id, CancellationToken ct = default)
    {
        var token = await GetAdminTokenAsync(ct).ConfigureAwait(false);
        var client = await GetRepresentationAsync(id, token, ct).ConfigureAwait(false);
        return client is null ? null : ToDetail(client);
    }

    public async Task<(OidcClientDetail Client, string Secret)> CreateClientAsync(
        CreateOidcClientSpec spec, CancellationToken ct = default)
    {
        var token = await GetAdminTokenAsync(ct).ConfigureAwait(false);

        var body = new
        {
            clientId = spec.ClientId,
            description = spec.Description,
            enabled = true,
            publicClient = false, // confidential — has a secret
            serviceAccountsEnabled = spec.ServiceAccountsEnabled,
            standardFlowEnabled = spec.RedirectUris is { Count: > 0 },
            redirectUris = spec.RedirectUris ?? [],
        };

        using var create = new HttpRequestMessage(HttpMethod.Post, $"/admin/realms/{_realm}/clients")
        {
            Content = JsonContent.Create(body),
        };
        create.Headers.Authorization = Bearer(token);

        var createResp = await _httpClient.SendAsync(create, ct).ConfigureAwait(false);
        createResp.EnsureSuccessStatusCode();

        // Keycloak returns 201 with the new resource URL in Location; the id is its last non-empty
        // segment (guard against a trailing slash yielding an empty id).
        var id = createResp.Headers.Location?.Segments
            .Select(s => s.Trim('/'))
            .LastOrDefault(s => !string.IsNullOrEmpty(s))
            ?? throw new InvalidOperationException(
                "Keycloak create-client response missing a usable Location header");

        var representation = await GetRepresentationAsync(id, token, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Client {id} not found after creation");
        var secret = await GetSecretAsync(id, token, ct).ConfigureAwait(false);

        _logger.LogInformation("Created OIDC client {ClientId} (id {Id})", spec.ClientId, id);
        return (ToDetail(representation), secret);
    }

    public async Task<string> RotateSecretAsync(string id, CancellationToken ct = default)
    {
        var token = await GetAdminTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/admin/realms/{_realm}/clients/{id}/client-secret");
        request.Headers.Authorization = Bearer(token);

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var credential = await response.Content
            .ReadFromJsonAsync<CredentialRepresentation>(cancellationToken: ct)
            .ConfigureAwait(false);
        _logger.LogInformation("Rotated secret for OIDC client {Id}", id);
        return credential?.Value
            ?? throw new InvalidOperationException("Keycloak rotate-secret response missing value");
    }

    public async Task<OidcClientDetail> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var token = await GetAdminTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/admin/realms/{_realm}/clients/{id}")
        {
            Content = JsonContent.Create(new { enabled }),
        };
        request.Headers.Authorization = Bearer(token);

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var representation = await GetRepresentationAsync(id, token, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Client {id} not found after update");
        return ToDetail(representation);
    }

    public async Task DeleteClientAsync(string id, CancellationToken ct = default)
    {
        var token = await GetAdminTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/realms/{_realm}/clients/{id}");
        request.Headers.Authorization = Bearer(token);

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Deleted OIDC client {Id}", id);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    private async Task<ClientRepresentation?> GetRepresentationAsync(
        string id, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/realms/{_realm}/clients/{id}");
        request.Headers.Authorization = Bearer(token);

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<ClientRepresentation>(cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private async Task<string> GetSecretAsync(string id, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/admin/realms/{_realm}/clients/{id}/client-secret");
        request.Headers.Authorization = Bearer(token);

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var credential = await response.Content
            .ReadFromJsonAsync<CredentialRepresentation>(cancellationToken: ct)
            .ConfigureAwait(false);
        // An empty secret is unusable; surface it rather than handing back a blank credential.
        return string.IsNullOrEmpty(credential?.Value)
            ? throw new InvalidOperationException("Keycloak client-secret response missing value")
            : credential!.Value!;
    }

    private async Task<string> GetAdminTokenAsync(CancellationToken ct)
    {
        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", _adminClientId),
            new KeyValuePair<string, string>("client_secret", _adminClientSecret),
        ]);

        var response = await _httpClient.PostAsync(
            $"/realms/{_realm}/protocol/openid-connect/token", content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content
            .ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
            .ConfigureAwait(false);
        return tokenResponse?.AccessToken
               ?? throw new InvalidOperationException("Keycloak token response missing access_token");
    }

    private static OidcClientSummary ToSummary(ClientRepresentation c) =>
        new(c.Id, c.ClientId, c.Enabled ?? true, c.ServiceAccountsEnabled ?? false, c.Description);

    private static OidcClientDetail ToDetail(ClientRepresentation c) =>
        new(c.Id, c.ClientId, c.Enabled ?? true, c.ServiceAccountsEnabled ?? false,
            c.PublicClient ?? false, c.Description, c.RedirectUris ?? []);

    private record ClientRepresentation(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("clientId")] string ClientId,
        [property: JsonPropertyName("enabled")] bool? Enabled,
        [property: JsonPropertyName("serviceAccountsEnabled")] bool? ServiceAccountsEnabled,
        [property: JsonPropertyName("publicClient")] bool? PublicClient,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("redirectUris")] string[]? RedirectUris);

    private record CredentialRepresentation(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("value")] string? Value);

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken);
}
