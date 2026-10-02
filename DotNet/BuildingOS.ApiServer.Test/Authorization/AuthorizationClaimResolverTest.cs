using System.Security.Claims;
using BuildingOs.ApiServer.Middlewares;

namespace BuildingOS.ApiServer.Test.Authorization;

public class AuthorizationClaimResolverTest
{
    private static Claim Sub(string id) => new(ClaimTypes.NameIdentifier, id);

    [Fact]
    public void AppToken_ResolvesToAdmin()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app"), Sub("svc1")]);

        Assert.NotNull(ctx);
        Assert.Equal("admin", ctx!.Role);
        Assert.True(ctx.IsAdmin);
        Assert.Equal("svc1", ctx.UserId);
    }

    [Fact]
    public void AppToken_WithoutUserId_FallsBackToAppId()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app")]);

        Assert.NotNull(ctx);
        Assert.Equal("app", ctx!.UserId);
        Assert.Equal("admin", ctx.Role);
    }

    /// <summary>
    /// #506: an application's client-credentials service account is given the group-manager role
    /// through the same building_os_role claim users get — the one opt-out from "app token = admin".
    /// </summary>
    [Fact]
    public void AppToken_CarryingGroupManager_IsAGroupManagerNotAnAdmin()
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
            [new Claim("idtyp", "app"), Sub("svc-portal"), new Claim("building_os_role", "group-manager")]);

        Assert.NotNull(ctx);
        Assert.Equal("group-manager", ctx!.Role);
        Assert.False(ctx.IsAdmin);
        Assert.True(ctx.CanManageGroups);
        Assert.Equal("svc-portal", ctx.UserId);
        Assert.Empty(ctx.Permissions);
    }

    /// <summary>
    /// Ownership of Groups is keyed on the subject, so a group-manager needs a real one: an app token
    /// without sub would share the "app" fallback with every other such client. Fail closed.
    /// </summary>
    [Fact]
    public void AppToken_GroupManagerWithoutASubject_GetsNothing()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app"), new Claim("building_os_role", "group-manager")]);

        Assert.False(ctx!.IsAdmin);
        Assert.False(ctx.IsGroupManager);
        Assert.Empty(ctx.Permissions);
    }

    /// <summary>
    /// Keycloak (24+) marks a service-account token with <c>client_id</c> (the default service_account
    /// scope), not with Azure AD's <c>idtyp=app</c>. Such a token carrying group-manager is a group-manager.
    /// </summary>
    [Theory]
    [InlineData("client_id")]
    [InlineData("clientId")]   // Keycloak before 24
    public void KeycloakServiceAccountToken_CarryingGroupManager_IsAGroupManager(string clientIdClaim)
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
            [Sub("svc-portal"), new Claim(clientIdClaim, "tenant-portal"), new Claim("building_os_role", "group-manager")]);

        Assert.True(ctx!.IsGroupManager);
        Assert.False(ctx.IsAdmin);
        Assert.Equal("svc-portal", ctx.UserId);
    }

    /// <summary>A Keycloak service-account token is not made an admin by being one (unlike idtyp=app).</summary>
    [Fact]
    public void KeycloakServiceAccountToken_KeepsItsOwnRole()
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
            [Sub("svc-x"), new Claim("client_id", "x"), new Claim("building_os_role", "viewer")]);

        Assert.Equal("viewer", ctx!.Role);
    }

    /// <summary>
    /// group-manager is a client-credentials role only. On a user token — where Keycloak's single-valued
    /// mapper may pick it over the user's viewer/operator role — it grants nothing.
    /// </summary>
    [Fact]
    public void UserToken_GroupManager_GetsNothing()
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
            [Sub("u1"), new Claim("building_os_role", "group-manager"), new Claim("permissions", "group:x:read")]);

        Assert.False(ctx!.IsGroupManager);
        Assert.False(ctx.CanManageGroups);
        Assert.False(ctx.ReadsWholeTwinStructure);
        Assert.Empty(ctx.Permissions);   // nothing — not even the grants that came with it
    }

    /// <summary>
    /// Any other role claim on an app token changes nothing: existing client-credentials clients stay
    /// admin exactly as before, whatever attribute their service account happens to carry.
    /// </summary>
    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("admin")]
    [InlineData("Group-Manager")]
    public void AppToken_WithAnyOtherRoleClaim_StaysAdmin(string role)
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
            [new Claim("idtyp", "app"), Sub("svc1"), new Claim("building_os_role", role)]);

        Assert.True(ctx!.IsAdmin);
    }

    [Fact]
    public void KeycloakNativeClaims_AreResolved()
    {
        // realm emits building_os_role (single) + permissions (multivalued → one Claim per value)
        var ctx = AuthorizationClaimResolver.TryResolve(
        [
            Sub("user1"),
            new Claim("building_os_role", "operator"),
            new Claim("permissions", "building:*:read"),
            new Claim("permissions", "point:*:read,control"),
        ]);

        Assert.NotNull(ctx);
        Assert.Equal("operator", ctx!.Role);
        Assert.Equal("user1", ctx.UserId);
        Assert.Equal(new[] { "building:*:read", "point:*:read,control" }, ctx.Permissions);
    }

    [Fact]
    public void LegacyAzureAdClaims_StillResolve()
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
        [
            Sub("user2"),
            new Claim("extension_BuildingOS_role", "viewer"),
            new Claim("extension_BuildingOS_permissions", "building:*:read"),
        ]);

        Assert.NotNull(ctx);
        Assert.Equal("viewer", ctx!.Role);
        Assert.Equal(new[] { "building:*:read" }, ctx.Permissions);
    }

    [Fact]
    public void NativeRoleTakesPrecedence_OverLegacy()
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
        [
            Sub("user3"),
            new Claim("building_os_role", "operator"),
            new Claim("extension_BuildingOS_role", "viewer"),
        ]);

        Assert.Equal("operator", ctx!.Role);
    }

    [Fact]
    public void RoleClaim_WithNoPermissions_ResolvesEmpty()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([Sub("user4"), new Claim("building_os_role", "viewer")]);

        Assert.NotNull(ctx);
        Assert.Empty(ctx!.Permissions);
    }

    [Fact]
    public void NoAuthzClaims_ReturnsNull_SoCallerFallsBackToAdminApi()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([Sub("user5"), new Claim("name", "Someone")]);

        Assert.Null(ctx);
    }
}
