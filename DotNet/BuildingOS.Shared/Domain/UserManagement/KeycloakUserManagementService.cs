using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Domain.UserManagement;

public class KeycloakUserManagementService : IUserManagementService
{
    private readonly HttpClient _httpClient;
    private readonly string _realm;
    private readonly string _adminClientId;
    private readonly string _adminClientSecret;
    private readonly ILogger<KeycloakUserManagementService> _logger;

    public KeycloakUserManagementService(
        HttpClient httpClient,
        string realm,
        string adminClientId,
        string adminClientSecret,
        ILogger<KeycloakUserManagementService> logger)
    {
        _httpClient = httpClient;
        _realm = realm;
        _adminClientId = adminClientId;
        _adminClientSecret = adminClientSecret;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EntraUser>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/admin/realms/{_realm}/users?max=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var users = await response.Content.ReadFromJsonAsync<KeycloakUserDto[]>(
            cancellationToken: cancellationToken);
        return users?.Select(MapToEntraUser).ToList() ?? [];
    }

    public async Task<EntraUser?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        var user = await GetUserJsonAsync(token, userId, cancellationToken);
        var dto = user?.Deserialize<KeycloakUserDto>();
        return dto == null ? null : MapToEntraUser(dto);
    }

    private async Task<JsonObject?> GetUserJsonAsync(
        string token, string userId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/admin/realms/{_realm}/users/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonObject>(
            cancellationToken: cancellationToken);
    }

    public async Task<EntraUser> UpdateUserAttributesAsync(
        string userId,
        UpdateUserAttributesRequest updateRequest,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);

        // Read-modify-write of the full representation (#519):
        // - Keycloak replaces the whole attribute map on PUT, so a permissions-only update must send
        //   the role back, and attributes that are not ours must survive.
        // - Since Keycloak 24 (user profile) a PUT carrying `attributes` is a full profile update: an
        //   absent email / firstName / lastName is cleared, which then blocks login ("Account is not
        //   fully set up"). Sending the representation we just read back keeps them.
        // - The legacy buildingos_* attributes are migrated into role / permissions and removed.
        var user = await GetUserJsonAsync(token, userId, cancellationToken)
                   ?? throw new InvalidOperationException($"User {userId} not found");
        var currentAttributes = user["attributes"]?.Deserialize<Dictionary<string, string[]>>();
        var attributes = KeycloakUserAttributes.BuildUpdate(
            currentAttributes, updateRequest.Role, updateRequest.Permissions);
        user["attributes"] = JsonSerializer.SerializeToNode(attributes);

        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/admin/realms/{_realm}/users/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(user);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation(
            "Updated Keycloak attributes for user {UserId}: role={Role}, permissions={Count}",
            userId, updateRequest.Role, updateRequest.Permissions?.Count ?? 0);

        var updated = await GetUserByIdAsync(userId, cancellationToken);
        return updated ?? throw new InvalidOperationException($"User {userId} not found after update");
    }

    public async Task<EntraUser> SetEnabledAsync(
        string userId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/admin/realms/{_realm}/users/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { enabled });

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Set enabled={Enabled} for Keycloak user {UserId}", enabled, userId);

        var updated = await GetUserByIdAsync(userId, cancellationToken);
        return updated ?? throw new InvalidOperationException($"User {userId} not found after update");
    }

    private async Task<string> GetAdminTokenAsync(CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", _adminClientId),
            new KeyValuePair<string, string>("client_secret", _adminClientSecret)
        ]);

        var response = await _httpClient.PostAsync(
            $"/realms/{_realm}/protocol/openid-connect/token",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<KeycloakTokenResponse>(
            cancellationToken: cancellationToken);
        return tokenResponse?.AccessToken
               ?? throw new InvalidOperationException("Keycloak token response missing access_token");
    }

    private static EntraUser MapToEntraUser(KeycloakUserDto dto)
    {
        var hasName = !string.IsNullOrEmpty(dto.FirstName) || !string.IsNullOrEmpty(dto.LastName);
        var displayName = hasName
            ? $"{dto.FirstName} {dto.LastName}".Trim()
            : dto.Username;

        // role / permissions, with the pre-#519 buildingos_* attributes honoured as a fallback.
        var role = KeycloakUserAttributes.ReadRole(dto.Attributes);
        var permissions = KeycloakUserAttributes.ReadPermissions(dto.Attributes);

        return new EntraUser
        {
            Id = dto.Id,
            DisplayName = displayName,
            Email = dto.Email,
            UserPrincipalName = dto.Username,
            Role = role,
            Permissions = permissions,
            // Keycloak omits `enabled` only on legacy records; treat a missing flag as enabled.
            Enabled = dto.Enabled ?? true
        };
    }

    private record KeycloakUserDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("firstName")] string? FirstName,
        [property: JsonPropertyName("lastName")] string? LastName,
        [property: JsonPropertyName("attributes")] Dictionary<string, string[]>? Attributes,
        [property: JsonPropertyName("enabled")] bool? Enabled = null);

    private record KeycloakTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
