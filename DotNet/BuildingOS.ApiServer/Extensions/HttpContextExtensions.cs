using System.Security.Claims;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOs.ApiServer.Middlewares;

namespace BuildingOs.ApiServer.Extensions;

public static class HttpContextExtensions
{
    public static AuthorizationContext GetAuthorizationContext(this HttpContext context)
    {
        // ミドルウェアで解決済みのコンテキストを優先使用
        if (context.Items.TryGetValue(AuthorizationContextMiddleware.HttpContextKey, out var cached)
            && cached is AuthorizationContext authContext)
        {
            return authContext;
        }

        // フォールバック: クレームから直接構築（ミドルウェア未登録時の後方互換性）。
        // ミドルウェアと同じ AuthorizationClaimResolver を使い、Keycloak ネイティブ（building_os_role /
        // permissions）と Azure-AD 互換（extension_BuildingOS_*）の両方を解決する。
        var claims = context.User.Claims.ToList();

        var fromClaims = AuthorizationClaimResolver.TryResolve(claims);
        if (fromClaims != null)
        {
            return fromClaims;
        }

        var userId = AuthorizationClaimResolver.GetUserId(claims)
            ?? throw new InvalidOperationException("Missing user identifier claim");

        return new AuthorizationContext
        {
            UserId = userId,
            Role = "user",
            Permissions = Array.Empty<string>()
        };
    }
}
