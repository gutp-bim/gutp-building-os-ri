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
        var userService = context.RequestServices.GetService<IUserManagementService>();
        if (userService != null)
        {
            try
            {
                var objectId = GetObjectId(claims) ?? userId;
                var user = await userService.GetUserByIdAsync(objectId).ConfigureAwait(false);
                if (user != null)
                {
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
