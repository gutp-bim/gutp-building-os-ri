using System.Security.Claims;
using BuildingOS.Shared.Domain.Authorization;

namespace BuildingOs.ApiServer.Middlewares;

/// <summary>
/// Pure resolution of an <see cref="AuthorizationContext"/> from JWT claims alone (no I/O). Covers the
/// two token-only paths: client-credential (<c>idtyp=app</c>) → admin, and a user token that already
/// carries the Building OS role/permission claims. Returns <c>null</c> when neither applies, so the
/// caller falls back to the Keycloak Admin API lookup.
///
/// Claim names are read **Keycloak-native first** (<c>building_os_role</c> / <c>permissions</c>, as
/// emitted by the <c>building-os-api</c> client scope mappers) with the legacy Azure-AD optional-claim
/// names (<c>extension_BuildingOS_*</c>, still emitted by <c>TestAuthenticationHandler</c>) kept as a
/// fallback. This is the #10 sign-off fix: previously only the Azure-AD names were read, so real
/// Keycloak tokens missed the claim path and every request hit the Admin API.
/// </summary>
public static class AuthorizationClaimResolver
{
    public const string RoleClaim = "building_os_role";
    public const string PermissionsClaim = "permissions";
    public const string LegacyRoleClaim = "extension_BuildingOS_role";
    public const string LegacyPermissionsClaim = "extension_BuildingOS_permissions";

    public static AuthorizationContext? TryResolve(IReadOnlyCollection<Claim> claims)
    {
        var userId = GetUserId(claims);

        // Client-credential (app) token → admin, even without a user id.
        var idtyp = claims.FirstOrDefault(c => c.Type == "idtyp")?.Value;
        if (idtyp == "app")
        {
            return new AuthorizationContext
            {
                UserId = userId ?? "app",
                Role = "admin",
                Permissions = Array.Empty<string>(),
            };
        }

        // User token carrying the Building OS authz claims (Keycloak-native, Azure-AD fallback).
        var role = claims.FirstOrDefault(c => c.Type == RoleClaim)?.Value
                ?? claims.FirstOrDefault(c => c.Type == LegacyRoleClaim)?.Value;
        if (role is null)
        {
            return null; // no token-only context — caller falls back to the Admin API.
        }

        var permissions = claims
            .Where(c => c.Type == PermissionsClaim || c.Type == LegacyPermissionsClaim)
            .Select(c => c.Value)
            .ToList();

        return new AuthorizationContext
        {
            UserId = userId ?? "unknown",
            Role = role,
            Permissions = permissions,
        };
    }

    public static string? GetUserId(IReadOnlyCollection<Claim> claims) =>
        claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
        ?? claims.FirstOrDefault(c => c.Type == "sub")?.Value
        ?? claims.FirstOrDefault(c => c.Type == "oid")?.Value
        ?? claims.FirstOrDefault(c => c.Type == "http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
}
