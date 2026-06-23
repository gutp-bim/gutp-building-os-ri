using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    private const string RoleAttribute = "buildingos_role";
    private const string PermissionsAttribute = "buildingos_permissions";

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
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/admin/realms/{_realm}/users/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var user = await response.Content.ReadFromJsonAsync<KeycloakUserDto>(
            cancellationToken: cancellationToken);
        return user == null ? null : MapToEntraUser(user);
    }

    public async Task<EntraUser> UpdateUserAttributesAsync(
        string userId,
        UpdateUserAttributesRequest updateRequest,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);

        var attributes = new Dictionary<string, string[]>();
        if (updateRequest.Role != null)
            attributes[RoleAttribute] = [updateRequest.Role];
        if (updateRequest.Permissions != null)
            attributes[PermissionsAttribute] = [.. updateRequest.Permissions];

        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/admin/realms/{_realm}/users/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { attributes });

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

        var role = dto.Attributes?.TryGetValue(RoleAttribute, out var roles) == true
            ? roles?.FirstOrDefault()
            : null;

        var permissions = dto.Attributes?.TryGetValue(PermissionsAttribute, out var perms) == true
            ? (IReadOnlyList<string>)(perms ?? [])
            : [];

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
