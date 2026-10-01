using System.Security.Claims;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.UserManagement;
using Microsoft.Extensions.Caching.Memory;

namespace BuildingOs.ApiServer.Middlewares;

/// <summary>
/// 認証済みユーザーのAuthorizationContextを解決し、HttpContext.Itemsに格納するミドルウェア。
/// 1. JWTクレームにカスタム属性がある場合はそれを使用（開発環境のTestAuthenticationHandler等）
/// 2. なければGraph API経由でCustom Security Attributesを取得（本番環境）
/// 3. 結果はIMemoryCacheでキャッシュ（5分間）
/// </summary>
public class AuthorizationContextMiddleware
{
    public const string HttpContextKey = "AuthorizationContext";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> WarnedAppRoles = new();

    private static bool IsAppToken(IEnumerable<System.Security.Claims.Claim> claims)
        => claims.Any(c => c.Type == "idtyp" && c.Value == "app");

    private readonly RequestDelegate _next;
    private readonly ILogger<AuthorizationContextMiddleware> _logger;

    public AuthorizationContextMiddleware(RequestDelegate next, ILogger<AuthorizationContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IMemoryCache cache)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var authContext = await ResolveAuthorizationContextAsync(context, cache).ConfigureAwait(false);
            // #506: group-manager is a client-credentials role. A user resolved to it (e.g. through the
            // Admin-API fallback) gets nothing, exactly as a user token carrying it does.
            if (authContext.IsGroupManager && !IsAppToken(context.User.Claims))
                authContext = authContext with { Role = AuthorizationClaimResolver.NoRole };
            context.Items[HttpContextKey] = authContext;
        }

        await _next(context).ConfigureAwait(false);
    }

    private async Task<AuthorizationContext> ResolveAuthorizationContextAsync(
        HttpContext context, IMemoryCache cache)
    {
        var claims = context.User.Claims.ToList();

        var userId = AuthorizationClaimResolver.GetUserId(claims);

        // 0-1. Token-only resolution: client-credential (idtyp=app) → admin, or a user token already
        //      carrying the Building OS role/permission claims (Keycloak-native building_os_role /
        //      permissions, with the legacy Azure-AD extension_BuildingOS_* names as a fallback).
        //      No I/O, so no per-request Keycloak Admin API call on the common path (#10 sign-off fix).
        var fromClaims = AuthorizationClaimResolver.TryResolve(claims);
        if (fromClaims != null)
        {
            // #506: an app token is downgraded only by exactly building_os_role=group-manager. Any other
            // role claim on it is ignored and the client stays admin — almost certainly a misconfigured
            // group-manager (wrong case, stray space), so say so instead of failing open silently.
            // Once per client and value: app tokens resolve on every request, uncached.
            var appRole = claims.FirstOrDefault(c => c.Type == AuthorizationClaimResolver.RoleClaim)?.Value
                          ?? claims.FirstOrDefault(c => c.Type == AuthorizationClaimResolver.LegacyRoleClaim)?.Value;
            if (fromClaims.IsAdmin && IsAppToken(claims)
                && !string.IsNullOrEmpty(appRole) && appRole != "admin"
                && WarnedAppRoles.TryAdd($"{fromClaims.UserId}\n{appRole}", 0))
            {
                _logger.LogWarning(
                    "Client-credentials token for {UserId} carries building_os_role {Role}, which only " +
                    "'group-manager' (exact) changes; the client is treated as admin", fromClaims.UserId, appRole);
            }
            if (!fromClaims.IsAdmin && !fromClaims.IsGroupManager && IsAppToken(claims) && userId is null
                && WarnedAppRoles.TryAdd("\nno-subject", 0))
            {
                _logger.LogWarning(
                    "A client-credentials token carries building_os_role group-manager but no subject (sub); " +
                    "Group ownership needs one, so the client gets no access. Add the 'basic' client scope");
            }
            return fromClaims;
        }

        if (userId == null)
        {
            _logger.LogWarning("No user identifier found in claims");
            return new AuthorizationContext { UserId = "unknown", Role = "user", Permissions = Array.Empty<string>() };
        }

        // 2. キャッシュ（Admin API フォールバック経路のみ）。トークンにクレームが載っていれば 0-1 で返るため、
        //    ここに来るのは Admin API 解決が必要なケースに限られる。
        var cacheKey = $"auth_context:{userId}";
        if (cache.TryGetValue(cacheKey, out AuthorizationContext? cached) && cached != null)
        {
            return cached;
        }

        // 3. Keycloak Admin API 経由で role/permissions を取得（トークンにクレームが無い場合のフォールバック）。
        //    属性名はトークン経路と同じ `role` / `permissions`（KeycloakUserAttributes が唯一の定義）。
        //    移行期間中は #519 以前の buildingos_* も KeycloakUserManagementService が読み合わせる。
        //    user.Role はトークンと同じ優先順（自身の role → グループの role → 旧 buildingos_role）で、
        //    /admin の表示とロックアウトガードも同じ値を使う。グループ間で role が食い違う場合は非 admin 側
        //    （fail-closed）。
        var userService = context.RequestServices.GetService<IUserManagementService>();
        if (userService != null)
        {
            try
            {
                var objectId = GetObjectId(claims) ?? userId;
                var user = await userService.GetUserByIdAsync(objectId).ConfigureAwait(false);
                if (user != null)
                {
                    // The user's groups could not be read (#532): the group role is unknown. Only the user's
                    // OWN `role` attribute is trusted then — never the legacy buildingos_role, which a group role
                    // would hide (own → group → legacy), so it could turn e.g. a viewer-group member into an
                    // admin. Without an own role this fails closed (role=user, no permissions) exactly as before
                    // #532. Not cached, so the next request retries the groups.
                    if (user.GroupRoleUnresolved)
                    {
                        if (string.IsNullOrEmpty(user.OwnAttributeRole))
                        {
                            _logger.LogWarning(
                                "Groups of user {UserId} could not be read and they have no own role; " +
                                "defaulting to role=user with no permissions", userId);
                            return new AuthorizationContext
                            {
                                UserId = userId, Role = "user", Permissions = Array.Empty<string>()
                            };
                        }

                        var ownContext = new AuthorizationContext
                        {
                            UserId = userId,
                            Role = user.OwnAttributeRole,
                            Permissions = user.Permissions.ToList()
                        };
                        _logger.LogWarning(
                            "Groups of user {UserId} could not be read; authorizing from their own role attribute: " +
                            "role={Role}, permissions={PermissionCount}",
                            userId, ownContext.Role, ownContext.Permissions.Count);
                        return ownContext;
                    }

                    var authContext = new AuthorizationContext
                    {
                        UserId = userId,
                        Role = user.Role ?? "user",
                        Permissions = user.Permissions.ToList()
                    };

                    cache.Set(cacheKey, authContext, CacheDuration);

                    _logger.LogDebug(
                        "Resolved authorization context via Admin API for user {UserId}: role={Role}, permissions={PermissionCount}",
                        userId, authContext.Role, authContext.Permissions.Count);

                    return authContext;
                }
            }
            catch (UserManagementUnavailableException)
            {
                // Keycloak admin is simply not configured (#293). That is a steady state, not a
                // failure, so it must not log an error on every request — the fallback below is the
                // intended behaviour. Before the unconfigured service existed this branch was skipped
                // entirely because the service resolved to null.
                _logger.LogDebug(
                    "User management not configured; resolving authorization context from claims only for {UserId}",
                    userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve authorization context for user {UserId} via Admin API", userId);
            }
        }

        // 4. フォールバック（Graph未設定 or 取得失敗）
        _logger.LogWarning(
            "No custom security attributes found for user {UserId}, defaulting to role=user with no permissions",
            userId);
        return new AuthorizationContext { UserId = userId, Role = "user", Permissions = Array.Empty<string>() };
    }

    private static string? GetObjectId(List<Claim> claims)
    {
        // Azure ADのobject ID（Graph APIのユーザーIDと一致）
        return claims.FirstOrDefault(c => c.Type == "oid")?.Value
            ?? claims.FirstOrDefault(c => c.Type == "http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
    }
}
