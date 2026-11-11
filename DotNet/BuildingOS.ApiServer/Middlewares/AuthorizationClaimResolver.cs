using System.Security.Claims;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.UserManagement;

namespace BuildingOs.ApiServer.Middlewares;

/// <summary>
/// Pure resolution of an <see cref="AuthorizationContext"/> from JWT claims alone (no I/O). Covers the
/// two token-only paths: a client-credential token (<c>idtyp=app</c>) and a user token that already
/// carries the Building OS role/permission claims. A client-credential token is authorized by the role and
/// permissions it carries, like a user token, and gets nothing without a role (#667); under
/// <see cref="ClientCredentialsRolePolicy.LegacyAdmin"/> it is admin unless it is a group-manager (#506). Returns <c>null</c> when neither applies, so the
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

    /// <summary>A role that grants nothing (the same "user" the middleware falls back to).</summary>
    public const string NoRole = "user";

    public static AuthorizationContext? TryResolve(
        IReadOnlyCollection<Claim> claims,
        ClientCredentialsRolePolicy appTokenPolicy = ClientCredentialsRolePolicy.Explicit)
    {
        var userId = GetUserId(claims);

        var role = claims.FirstOrDefault(c => c.Type == RoleClaim)?.Value
                ?? claims.FirstOrDefault(c => c.Type == LegacyRoleClaim)?.Value;

        // Client-credential (app) token. An application's service account given the group-manager role
        // (#506) keeps Groups in sync without full admin, under either policy.
        var idtyp = claims.FirstOrDefault(c => c.Type == "idtyp")?.Value;
        if (idtyp == "app")
        {
            if (role == RoleCatalog.GroupManager)
            {
                // Group ownership is keyed on the subject; without one the client would share the
                // "app" fallback id with every other such client — so it gets nothing instead.
                return new AuthorizationContext
                {
                    UserId = userId ?? "app",
                    Role = userId is null ? NoRole : RoleCatalog.GroupManager,
                    Permissions = Array.Empty<string>(),
                };
            }
            if (appTokenPolicy == ClientCredentialsRolePolicy.LegacyAdmin)
            {
                // Pre-#667: any other (or no) role claim leaves the client admin.
                return new AuthorizationContext
                {
                    UserId = userId ?? "app",
                    Role = RoleCatalog.Admin,
                    Permissions = Array.Empty<string>(),
                };
            }
            if (role is null)
            {
                // #667: no role → nothing, never admin. Returned here rather than falling back to the
                // Keycloak Admin API: an app's subject is a service principal, not a Keycloak user.
                return new AuthorizationContext
                {
                    UserId = userId ?? "app",
                    Role = NoRole,
                    Permissions = Array.Empty<string>(),
                };
            }
            // Any other role: evaluated exactly like a user token below — admin only for an explicit
            // building_os_role=admin; a mis-cased or unknown role is never admin and leaves the client
            // only the permission strings it carries.
            userId ??= "app";
        }

        // User token carrying the Building OS authz claims (Keycloak-native, Azure-AD fallback).
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
            // group-manager is a client-credentials role (#506): honoured on a Keycloak service-account
            // token with a subject; on a user token — where Keycloak's single-valued mapper may emit it in
            // place of the user's own viewer/operator — it grants nothing. Never with permission strings.
            Role = role != RoleCatalog.GroupManager ? role
                : IsServiceAccountToken(claims) && userId is not null ? RoleCatalog.GroupManager
                : NoRole,
            Permissions = role == RoleCatalog.GroupManager ? Array.Empty<string>() : permissions,
        };
    }

    /// <summary>
    /// Whether the token was issued by the client-credentials grant: Azure AD's <c>idtyp=app</c>, or the
    /// client-id claim Keycloak's default <c>service_account</c> scope puts only on service-account tokens
    /// (<c>client_id</c> since Keycloak 24, <c>clientId</c> before). Being one grants nothing by itself
    /// (only <see cref="ClientCredentialsRolePolicy.LegacyAdmin"/> still makes <c>idtyp=app</c> an admin).
    /// </summary>
    public static bool IsServiceAccountToken(IReadOnlyCollection<Claim> claims)
        => claims.Any(c => (c.Type == "idtyp" && c.Value == "app")
                           || ((c.Type == "client_id" || c.Type == "clientId") && !string.IsNullOrEmpty(c.Value)));

    public static string? GetUserId(IReadOnlyCollection<Claim> claims) =>
        claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
        ?? claims.FirstOrDefault(c => c.Type == "sub")?.Value
        ?? claims.FirstOrDefault(c => c.Type == "oid")?.Value
        ?? claims.FirstOrDefault(c => c.Type == "http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
}
