using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Domain.UserManagement;

public class KeycloakUserManagementService : IUserManagementService
{
    /// <summary>The web defaults <c>ReadFromJsonAsync</c> uses (case-insensitive property matching).</summary>
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Profile fields a Keycloak 24+ (user profile) PUT carrying <c>attributes</c> must include, or they
    /// are cleared ("Account is not fully set up"). Everything else — notably <c>enabled</c>,
    /// <c>requiredActions</c> and <c>emailVerified</c> — is left out, so a concurrent change by another
    /// admin (e.g. <see cref="SetEnabledAsync"/>) is not reverted with the stale value this update read.
    /// </summary>
    private static readonly string[] ProfileFields = ["username", "email", "firstName", "lastName"];

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
        var users = await ListUserDtosAsync(token, cancellationToken);
        return users.Select(MapToEntraUser).ToList();
    }

    public async Task<IReadOnlyList<UserRoleState>> GetUserRoleStatesAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        var users = await ListUserDtosAsync(token, cancellationToken);

        // The building-os-role mapper is non-aggregating: the user's own `role` reaches the token, and
        // only without one does a group's (or a parent group's) `role` do so. Groups are therefore looked
        // up only for users with no own role, and each ancestor group at most once per call.
        var ancestorRoleByPath = new Dictionary<string, string?>(StringComparer.Ordinal);
        var states = new List<UserRoleState>(users.Count);
        foreach (var dto in users)
        {
            var own = KeycloakUserAttributes.ReadRole(dto.Attributes);
            var groupRole = own is null
                ? await GetGroupRoleAsync(token, dto.Id, ancestorRoleByPath, cancellationToken)
                : null;
            states.Add(new UserRoleState(dto.Id, own, dto.Enabled ?? true, groupRole));
        }
        return states;
    }

    private async Task<IReadOnlyList<KeycloakUserDto>> ListUserDtosAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/admin/realms/{_realm}/users?max=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<KeycloakUserDto[]>(
            cancellationToken: cancellationToken) ?? [];
    }

    /// <summary>
    /// The role a user inherits from their groups: each group's own <c>role</c>, else its nearest
    /// ancestor's. Keycloak does not define which of several groups wins for a non-aggregating mapper, so
    /// an admin grant from any group is reported as admin (the guard then errs on "still an admin").
    /// </summary>
    private async Task<string?> GetGroupRoleAsync(
        string token, string userId, Dictionary<string, string?> ancestorRoleByPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/admin/realms/{_realm}/users/{userId}/groups?briefRepresentation=false");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var groups = await response.Content.ReadFromJsonAsync<KeycloakGroupDto[]>(
            cancellationToken: cancellationToken) ?? [];

        string? first = null;
        foreach (var group in groups)
        {
            var role = KeycloakUserAttributes.ReadGroupRole(group.Attributes)
                       ?? await GetAncestorRoleAsync(token, group.Path, ancestorRoleByPath, cancellationToken);
            if (role is null) continue;
            if (RoleCatalog.GrantsAdmin(role)) return role;
            first ??= role;
        }
        return first;
    }

    /// <summary>The nearest ancestor group's role, walking up the group path (e.g. /ops/tokyo → /ops).</summary>
    private async Task<string?> GetAncestorRoleAsync(
        string token, string? path, Dictionary<string, string?> ancestorRoleByPath, CancellationToken cancellationToken)
    {
        for (var parent = ParentPath(path); parent is not null; parent = ParentPath(parent))
        {
            if (!ancestorRoleByPath.TryGetValue(parent, out var role))
            {
                role = await GetGroupRoleByPathAsync(token, parent, cancellationToken);
                ancestorRoleByPath[parent] = role;
            }
            if (role is not null) return role;
        }
        return null;
    }

    private async Task<string?> GetGroupRoleByPathAsync(string token, string path, CancellationToken cancellationToken)
    {
        var escaped = string.Join('/', path.Trim('/').Split('/').Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/admin/realms/{_realm}/group-by-path/{escaped}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var group = await response.Content.ReadFromJsonAsync<KeycloakGroupDto>(cancellationToken: cancellationToken);
        return KeycloakUserAttributes.ReadGroupRole(group?.Attributes);
    }

    private static string? ParentPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var idx = path.TrimEnd('/').LastIndexOf('/');
        return idx <= 0 ? null : path[..idx];
    }

    public async Task<EntraUser?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        using var response = await SendGetUserAsync(token, userId, cancellationToken);
        if (response is null) return null;

        var dto = await response.Content.ReadFromJsonAsync<KeycloakUserDto>(cancellationToken: cancellationToken);
        return dto == null ? null : MapToEntraUser(dto);
    }

    /// <summary>GETs the user representation; <c>null</c> when Keycloak answers 404.</summary>
    private async Task<HttpResponseMessage?> SendGetUserAsync(
        string token, string userId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/admin/realms/{_realm}/users/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        response.EnsureSuccessStatusCode();
        return response;
    }

    public async Task<EntraUser> UpdateUserAttributesAsync(
        string userId,
        UpdateUserAttributesRequest updateRequest,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);

        // Read-modify-write of the attribute map (#519):
        // - Keycloak replaces the whole attribute map on PUT, so a permissions-only update must send
        //   the role back, and attributes that are not ours must survive.
        // - Since Keycloak 24 (user profile) a PUT carrying `attributes` is a full profile update: an
        //   absent email / firstName / lastName is cleared, so those go back too (ProfileFields) — but
        //   nothing else: resending enabled / requiredActions / emailVerified from this snapshot would
        //   revert a concurrent change by another admin.
        // - The legacy buildingos_* attributes are migrated into role / permissions and removed.
        JsonObject user;
        using (var response = await SendGetUserAsync(token, userId, cancellationToken))
        {
            user = (response is null
                       ? null
                       : await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken))
                   ?? throw new InvalidOperationException($"User {userId} not found");
        }
        var dto = user.Deserialize<KeycloakUserDto>(WebJson)
                  ?? throw new InvalidOperationException($"User {userId} not found");

        // Throws ArgumentException for a role outside RoleCatalog — before anything is written.
        var attributes = KeycloakUserAttributes.BuildUpdate(
            dto.Attributes, updateRequest.Role, updateRequest.Permissions);

        var body = new JsonObject();
        foreach (var field in ProfileFields)
        {
            if (user.TryGetPropertyValue(field, out var value))
                body[field] = value?.DeepClone();
        }
        body["attributes"] = JsonSerializer.SerializeToNode(attributes);

        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/admin/realms/{_realm}/users/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);

        using var putResponse = await _httpClient.SendAsync(request, cancellationToken);
        putResponse.EnsureSuccessStatusCode();

        _logger.LogInformation(
            "Updated Keycloak attributes for user {UserId}: role={Role}, permissions={Count}",
            userId, updateRequest.Role, updateRequest.Permissions?.Count ?? 0);

        // The PUT stored exactly these attributes; the representation already read covers the rest, so
        // no second token request or re-GET is needed.
        return MapToEntraUser(dto with { Attributes = attributes });
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

    private record KeycloakGroupDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("attributes")] Dictionary<string, string[]>? Attributes);

    private record KeycloakTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
