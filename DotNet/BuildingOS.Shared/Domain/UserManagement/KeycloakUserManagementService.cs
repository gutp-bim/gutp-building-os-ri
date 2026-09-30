using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BuildingOS.Shared.Infrastructure.Telemetry;
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

    /// <summary>
    /// A group-path separator: a <c>/</c> not escaped as <c>~/</c>. Keycloak 23+ escapes a <c>/</c> inside a
    /// group name that way (and nothing else), so <c>/a~/b</c> is the one top-level group <c>a/b</c>.
    /// </summary>
    private static readonly Regex PathSeparator = new("(?<!~)/", RegexOptions.Compiled);

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
        var groups = new GroupRoleLookup(this, await GetAdminTokenAsync(cancellationToken));
        var resolved = await ResolveAllAsync(groups, cancellationToken);
        return resolved.Select(r => MapToEntraUser(r.Dto, r.Role)).ToList();
    }

    public async Task<IReadOnlyList<UserRoleState>> GetUserRoleStatesAsync(CancellationToken cancellationToken = default) =>
        await (await CreateRoleLookupAsync(cancellationToken)).GetAllAsync(cancellationToken);

    public async Task<IUserRoleLookup> CreateRoleLookupAsync(CancellationToken cancellationToken = default) =>
        new RoleLookup(this, new GroupRoleLookup(this, await GetAdminTokenAsync(cancellationToken)));

    /// <summary>One guard evaluation's lookups: one token, one group cache (<see cref="IUserRoleLookup"/>).</summary>
    private sealed class RoleLookup(KeycloakUserManagementService service, GroupRoleLookup groups) : IUserRoleLookup
    {
        public async Task<UserRoleState?> GetUserAsync(
            string userId, bool includeGroupRole, CancellationToken cancellationToken = default)
        {
            var user = await service.ReadUserAsync(groups.Token, userId, cancellationToken);
            if (user is null) return null;
            return (await ResolveRoleAsync(groups, user.Value.Dto, includeGroupRole, cancellationToken)).State;
        }

        public async Task<IReadOnlyList<UserRoleState>> GetAllAsync(CancellationToken cancellationToken = default) =>
            (await service.ResolveAllAsync(groups, cancellationToken)).Select(r => r.Role.State).ToList();
    }

    /// <summary>Upper bound on concurrent per-user group lookups when resolving the whole user list.</summary>
    private const int MaxConcurrentGroupLookups = 8;

    /// <summary>
    /// Lists users and resolves each one's role. Per-user group lookups run concurrently (bounded by
    /// <see cref="MaxConcurrentGroupLookups"/>); the result keeps Keycloak's user order.
    /// </summary>
    private async Task<IReadOnlyList<(KeycloakUserDto Dto, ResolvedRole Role)>> ResolveAllAsync(
        GroupRoleLookup groups, CancellationToken cancellationToken)
    {
        var users = await ListUserDtosAsync(groups.Token, cancellationToken);
        var results = new (KeycloakUserDto Dto, ResolvedRole Role)[users.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, users.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentGroupLookups, CancellationToken = cancellationToken },
            async (i, ct) => results[i] = (users[i], await ResolveRoleAsync(groups, users[i], includeGroupRole: false, ct)));
        return results;
    }

    public async Task<EntraUser?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        var user = await ReadUserAsync(token, userId, cancellationToken);
        if (user is null) return null;
        return await MapWithRoleAsync(token, user.Value.Dto, cancellationToken);
    }

    public async Task<EntraUser> UpdateUserAttributesAsync(
        string userId,
        UpdateUserAttributesRequest updateRequest,
        CancellationToken cancellationToken = default)
    {
        // One admin token for the whole read-modify-write-verify (#519):
        // - Keycloak replaces the whole attribute map on PUT, so attributes that are not being changed —
        //   including both role attributes on a permission-only write — go back exactly as stored.
        // - Since Keycloak 24 (user profile) a PUT carrying `attributes` is a full profile update: an
        //   absent email / firstName / lastName is cleared, so those go back too (ProfileFields) — but
        //   nothing else: resending enabled / requiredActions / emailVerified from this snapshot would
        //   revert a concurrent change by another admin.
        // - Permission add/remove is applied to the set read here, so the caller needs no read of its own.
        // - The PUT's 204 proves nothing (a realm without unmanagedAttributePolicy drops the attributes and
        //   still answers 204), so the user is read back and compared with what was written.
        var token = await GetAdminTokenAsync(cancellationToken);
        var (user, dto) = await ReadUserAsync(token, userId, cancellationToken)
                          ?? throw new UserNotFoundException(userId);

        var attributes = KeycloakUserAttributes.BuildUpdate(dto.Attributes, updateRequest);

        var body = new JsonObject();
        foreach (var field in ProfileFields)
        {
            if (user.TryGetPropertyValue(field, out var value))
                body[field] = value?.DeepClone();
        }
        body["attributes"] = JsonSerializer.SerializeToNode(attributes);
        await PutUserAsync(token, userId, body, cancellationToken);

        // From here on the PUT has been accepted, so a failure of the verifying read (5xx, timeout,
        // cancellation) does not mean the write failed — it means it is unverified (#532). The caller
        // treats that as written (success + warning), not as a 400 inviting a retry of a committed write.
        KeycloakUserDto stored;
        try
        {
            stored = (await ReadUserAsync(token, userId, cancellationToken))?.Dto
                     ?? throw new UserNotFoundException(userId);
        }
        catch (Exception ex) when (ex is not UserNotFoundException)
        {
            _logger.LogWarning(ex,
                "Keycloak accepted the attribute update for user {UserId} but the verifying read failed; " +
                "reporting it as written but unverified", userId);
            var ownRole = KeycloakUserAttributes.ReadMappedRole(attributes);
            var asWritten = MapToEntraUser(dto with { Attributes = attributes }, ownRole,
                groupRoleUnresolved: string.IsNullOrEmpty(ownRole));
            throw new UserAttributesWrittenUnverifiedException(userId, asWritten, ex);
        }

        // Only "dropped entirely" fails: a concurrent write by another admin between the PUT and this
        // read may legitimately change the values (see LooksPersisted).
        if (!KeycloakUserAttributes.LooksPersisted(attributes, stored.Attributes))
        {
            _logger.LogError(
                "Keycloak accepted the attribute update for user {UserId} but did not store it; " +
                "check the realm's unmanagedAttributePolicy (ADMIN_EDIT)", userId);
            throw new UserAttributesNotPersistedException(userId, KeycloakUserAttributes.Pick(stored.Attributes));
        }

        _logger.LogInformation(
            "Updated Keycloak attributes for user {UserId}: role={Role}, permissions={Count}",
            userId, updateRequest.Role,
            attributes.TryGetValue(KeycloakUserAttributes.Permissions, out var written) ? written.Length : 0);

        return await MapWithRoleAsync(token, stored, cancellationToken);
    }

    public async Task<EntraUser> SetEnabledAsync(
        string userId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        await PutUserAsync(token, userId, new JsonObject { ["enabled"] = enabled }, cancellationToken);

        _logger.LogInformation("Set enabled={Enabled} for Keycloak user {UserId}", enabled, userId);

        var updated = await ReadUserAsync(token, userId, cancellationToken)
                      ?? throw new UserNotFoundException(userId);
        return await MapWithRoleAsync(token, updated.Dto, cancellationToken);
    }

    // ── Role resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// A user's role state (for the guard) and the role that reaches authorization (for display);
    /// <paramref name="GroupRoleUnresolved"/> when that role depends on groups that could not be read (#532).
    /// </summary>
    private readonly record struct ResolvedRole(UserRoleState State, string? AuthorizationRole, bool GroupRoleUnresolved);

    /// <summary>
    /// Resolves what reaches authorization, in order: the user's own <c>role</c> (the mapper never reads
    /// the legacy name), else a group's <c>role</c>, else — no claim at all — the legacy
    /// <c>buildingos_role</c> the Admin-API fallback reads. Groups are looked up only without an own role.
    /// </summary>
    private static async Task<ResolvedRole> ResolveRoleAsync(
        GroupRoleLookup groups, KeycloakUserDto dto, bool includeGroupRole, CancellationToken cancellationToken)
    {
        var own = KeycloakUserAttributes.ReadMappedRole(dto.Attributes);
        var legacy = KeycloakUserAttributes.ReadLegacyRole(dto.Attributes);
        // An own role hides the group role in the token, so it is skipped — unless the caller needs it
        // (clearing the own role falls back to it).
        var groupRoles = own is null || includeGroupRole
            ? await groups.GetUserGroupRolesAsync(dto.Id, cancellationToken)
            : GroupRoles.None;

        var state = new UserRoleState(
            dto.Id, own, dto.Enabled ?? true, groupRoles.Conservative, groupRoles.Ambiguous, legacy,
            GroupRoleUnknown: groupRoles.Unknown);
        // Without an own role the groups decide; if they could not be read the role is unknown (#532) —
        // null, not the legacy value, which a group role would have hidden.
        if (groupRoles.Unknown && string.IsNullOrEmpty(own))
            return new ResolvedRole(state, null, GroupRoleUnresolved: true);
        // The Admin-API fallback authorizes from this value, so disagreeing groups fail closed.
        return new ResolvedRole(state, KeycloakUserAttributes.ResolveRole(own, groupRoles.FailClosed, legacy), false);
    }

    private async Task<EntraUser> MapWithRoleAsync(string token, KeycloakUserDto dto, CancellationToken cancellationToken) =>
        MapToEntraUser(dto,
            await ResolveRoleAsync(new GroupRoleLookup(this, token), dto, includeGroupRole: false, cancellationToken));

    /// <summary>
    /// The roles a user's groups would put into the token. Keycloak's non-aggregating mapper emits ONE of
    /// them and does not define which, so the guard's target view is conservative (admin if any group
    /// grants admin), the authorization view fails closed (a non-admin role if any group carries one), and
    /// disagreement is flagged.
    /// </summary>
    private readonly record struct GroupRoles(string? Conservative, string? FailClosed, bool Ambiguous, bool Unknown = false)
    {
        public static readonly GroupRoles None = new(null, null, false);

        /// <summary>The groups could not be read (#532): no role is known, which is not the same as none.</summary>
        public static readonly GroupRoles Unresolved = new(null, null, false, Unknown: true);

        public static GroupRoles From(IReadOnlyList<string> roles) =>
            roles.Count == 0
                ? None
                : new GroupRoles(
                    roles.FirstOrDefault(RoleCatalog.GrantsAdmin) ?? roles[0],
                    roles.FirstOrDefault(r => !RoleCatalog.GrantsAdmin(r)) ?? roles[0],
                    roles.Distinct(StringComparer.Ordinal).Skip(1).Any());
    }

    /// <summary>
    /// Resolves group roles with one admin token, caching each user's group roles and each ancestor group
    /// for the lifetime of one lookup. Safe for concurrent use: a key being fetched is shared, not refetched.
    /// A group's own <c>role</c> wins, else its nearest ancestor's — walked by <c>parentId</c> (Keycloak 23+),
    /// or, when a group carries none (Keycloak &lt; 23), by its path, honouring the <c>~/</c> escape.
    /// </summary>
    private sealed class GroupRoleLookup(KeycloakUserManagementService service, string token)
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<KeycloakGroupDto?>>> _byId = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<Task<KeycloakGroupDto?>>> _byPath = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<Task<GroupRoles>>> _byUser = new(StringComparer.Ordinal);

        public string Token => token;

        public Task<GroupRoles> GetUserGroupRolesAsync(string userId, CancellationToken cancellationToken) =>
            _byUser.GetOrAdd(userId, id => new Lazy<Task<GroupRoles>>(() => FetchUserGroupRolesAsync(id, cancellationToken)))
                .Value;

        /// <summary>
        /// The user's group roles, or <see cref="GroupRoles.Unresolved"/> when the groups (or an ancestor)
        /// cannot be read — a 403 from a service account without <c>query-groups</c>, a 5xx, a timeout (#532).
        /// One user's unreadable groups must not fail a whole user list, turn a committed write into a 400,
        /// or make the Admin-API fallback drop a user to <c>role=user</c>; the callers decide what "unknown"
        /// means for them. A cancellation of the caller's own token still propagates.
        /// </summary>
        private async Task<GroupRoles> FetchUserGroupRolesAsync(string userId, CancellationToken cancellationToken)
        {
            try
            {
                var groups = await service.GetJsonAsync<KeycloakGroupDto[]>(token,
                    $"users/{Uri.EscapeDataString(userId)}/groups?briefRepresentation=false", cancellationToken) ?? [];

                var roles = new List<string>(groups.Length);
                foreach (var group in groups)
                {
                    var role = KeycloakUserAttributes.ReadMappedRole(group.Attributes)
                               ?? await GetInheritedRoleAsync(group, cancellationToken);
                    if (role is not null) roles.Add(role);
                }
                return GroupRoles.From(roles);
            }
            catch (Exception ex) when (GroupLookupFailureReason(ex, cancellationToken) is { } reason)
            {
                BuildingOsMetrics.UserGroupLookupFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
                service._logger.LogWarning(ex,
                    "Could not read the Keycloak groups of user {UserId} ({Reason}); their group role is " +
                    "reported as unknown. Check that the admin service account has query-groups / view-users",
                    userId, reason);
                return GroupRoles.Unresolved;
            }
        }

        /// <summary>The metric reason for a failed group lookup; <c>null</c> for anything that must propagate.</summary>
        private static string? GroupLookupFailureReason(Exception ex, CancellationToken cancellationToken) => ex switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } => "forbidden",
            HttpRequestException => "http_error",
            // HttpClient's own timeout; the caller's cancellation is not a lookup failure.
            OperationCanceledException when !cancellationToken.IsCancellationRequested => "timeout",
            JsonException or NotSupportedException => "invalid_response",
            _ => null,
        };

        private async Task<string?> GetInheritedRoleAsync(KeycloakGroupDto group, CancellationToken cancellationToken)
        {
            for (var current = group; ;)
            {
                KeycloakGroupDto? parent;
                if (current.ParentId is { Length: > 0 } parentId)
                {
                    parent = await GetCachedAsync(_byId, parentId,
                        $"groups/{Uri.EscapeDataString(parentId)}", cancellationToken);
                }
                else
                {
                    var parentPath = ParentPath(current.Path);
                    if (parentPath is null) return null;
                    var escaped = string.Join('/',
                        PathSeparator.Split(parentPath.TrimStart('/')).Select(Uri.EscapeDataString));
                    parent = await GetCachedAsync(_byPath, parentPath, $"group-by-path/{escaped}", cancellationToken);
                }

                if (parent is null) return null;
                var role = KeycloakUserAttributes.ReadMappedRole(parent.Attributes);
                if (role is not null) return role;
                current = parent;
            }
        }

        private Task<KeycloakGroupDto?> GetCachedAsync(
            ConcurrentDictionary<string, Lazy<Task<KeycloakGroupDto?>>> cache, string key, string relativeUrl,
            CancellationToken cancellationToken) =>
            cache.GetOrAdd(key, _ => new Lazy<Task<KeycloakGroupDto?>>(
                () => service.GetJsonAsync<KeycloakGroupDto>(token, relativeUrl, cancellationToken))).Value;

        /// <summary>The parent of a group path (<c>/ops/tokyo</c> → <c>/ops</c>); <c>null</c> for a top-level group.</summary>
        private static string? ParentPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var separators = PathSeparator.Matches(path);
            var last = separators.Count == 0 ? -1 : separators[^1].Index;
            return last <= 0 ? null : path[..last];
        }
    }

    // ── HTTP ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<KeycloakUserDto>> ListUserDtosAsync(string token, CancellationToken cancellationToken) =>
        await GetJsonAsync<KeycloakUserDto[]>(token, "users?max=100", cancellationToken) ?? [];

    /// <summary>GETs the user representation (raw + typed); <c>null</c> when Keycloak answers 404.</summary>
    private async Task<(JsonObject Json, KeycloakUserDto Dto)?> ReadUserAsync(
        string token, string userId, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync<JsonObject>(token, $"users/{Uri.EscapeDataString(userId)}", cancellationToken);
        var dto = json?.Deserialize<KeycloakUserDto>(WebJson);
        return json is null || dto is null ? null : (json, dto);
    }

    /// <summary>GETs an Admin API resource relative to the realm; <c>null</c> on 404.</summary>
    private async Task<T?> GetJsonAsync<T>(string token, string relativeUrl, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/realms/{_realm}/{relativeUrl}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(WebJson, cancellationToken);
    }

    private async Task PutUserAsync(string token, string userId, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/admin/realms/{_realm}/users/{Uri.EscapeDataString(userId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new UserNotFoundException(userId);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetAdminTokenAsync(CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", _adminClientId),
            new KeyValuePair<string, string>("client_secret", _adminClientSecret)
        ]);

        using var response = await _httpClient.PostAsync(
            $"/realms/{_realm}/protocol/openid-connect/token",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<KeycloakTokenResponse>(
            cancellationToken: cancellationToken);
        return tokenResponse?.AccessToken
               ?? throw new InvalidOperationException("Keycloak token response missing access_token");
    }

    private static EntraUser MapToEntraUser(KeycloakUserDto dto, ResolvedRole role) =>
        MapToEntraUser(dto, role.AuthorizationRole, role.GroupRoleUnresolved);

    private static EntraUser MapToEntraUser(KeycloakUserDto dto, string? role, bool groupRoleUnresolved = false)
    {
        var hasName = !string.IsNullOrEmpty(dto.FirstName) || !string.IsNullOrEmpty(dto.LastName);
        var displayName = hasName
            ? $"{dto.FirstName} {dto.LastName}".Trim()
            : dto.Username;

        return new EntraUser
        {
            Id = dto.Id,
            DisplayName = displayName,
            Email = dto.Email,
            UserPrincipalName = dto.Username,
            Role = role,
            // permissions, with the pre-#519 buildingos_permissions merged in.
            Permissions = KeycloakUserAttributes.ReadPermissions(dto.Attributes),
            // Keycloak omits `enabled` only on legacy records; treat a missing flag as enabled.
            Enabled = dto.Enabled ?? true,
            GroupRoleUnresolved = groupRoleUnresolved,
            OwnAttributeRole = KeycloakUserAttributes.ResolveRole(
                KeycloakUserAttributes.ReadMappedRole(dto.Attributes), groupRole: null,
                KeycloakUserAttributes.ReadLegacyRole(dto.Attributes)),
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
        [property: JsonPropertyName("attributes")] Dictionary<string, string[]>? Attributes,
        [property: JsonPropertyName("parentId")] string? ParentId = null);

    private record KeycloakTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
