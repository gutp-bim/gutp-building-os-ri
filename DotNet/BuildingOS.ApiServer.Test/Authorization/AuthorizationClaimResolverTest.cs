using System.Security.Claims;
using BuildingOs.ApiServer.Middlewares;
using BuildingOS.Shared.Domain.Authorization;

namespace BuildingOS.ApiServer.Test.Authorization;

public class AuthorizationClaimResolverTest
{
    private static Claim Sub(string id) => new(ClaimTypes.NameIdentifier, id);

    private const ClientCredentialsRolePolicy Legacy = ClientCredentialsRolePolicy.LegacyAdmin;

    // ---- #667: explicit (default) — an app token gets only the role and permissions it carries ----

    /// <summary>#667: an app token without a role is not an admin — it gets nothing, and does not fall
    /// through to the Admin API (TryResolve returns a context, not null).</summary>
    [Fact]
    public void AppToken_WithoutARole_GetsNothing()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app"), Sub("svc1"), new Claim("permissions", "point:*:read,control")]);

        Assert.NotNull(ctx);
        Assert.False(ctx!.IsAdmin);
        Assert.Equal(AuthorizationClaimResolver.NoRole, ctx.Role);
        Assert.Empty(ctx.Permissions);
        Assert.Equal("svc1", ctx.UserId);
    }

    [Fact]
    public void AppToken_WithoutARoleOrUserId_GetsNothing_UnderTheAppId()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app")]);

        Assert.NotNull(ctx);
        Assert.Equal("app", ctx!.UserId);
        Assert.False(ctx.IsAdmin);
        Assert.Empty(ctx.Permissions);
    }

    [Fact]
    public void AppToken_WithAnExplicitAdminRole_IsAdmin()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app"), Sub("svc1"), new Claim("building_os_role", "admin")]);

        Assert.True(ctx!.IsAdmin);
        Assert.Equal("svc1", ctx.UserId);
    }

    /// <summary>A read-only integration: viewer plus read grants, exactly like a user token.</summary>
    [Fact]
    public void AppToken_Viewer_GetsItsRoleAndPermissions()
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
        [
            new Claim("idtyp", "app"), Sub("svc-reader"),
            new Claim("building_os_role", "viewer"), new Claim("permissions", "building:B1:read"),
        ]);

        Assert.Equal("viewer", ctx!.Role);
        Assert.False(ctx.IsAdmin);
        Assert.Equal(["building:B1:read"], ctx.Permissions);
    }

    [Fact]
    public void AppToken_Operator_GetsItsRoleAndPermissions()
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
        [
            new Claim("idtyp", "app"), Sub("svc-ctl"),
            new Claim("building_os_role", "operator"), new Claim("permissions", "point:P1:read,control"),
        ]);

        Assert.Equal("operator", ctx!.Role);
        Assert.False(ctx.IsAdmin);
        Assert.Equal(["point:P1:read,control"], ctx.Permissions);
    }

    /// <summary>A mis-spelled role fails closed: never admin.</summary>
    [Theory]
    [InlineData("Admin")]
    [InlineData(" admin")]
    [InlineData("Group-Manager")]
    [InlineData("superuser")]
    public void AppToken_WithAMisspelledRole_IsNotAdmin(string role)
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app"), Sub("svc1"), new Claim("building_os_role", role)]);

        Assert.False(ctx!.IsAdmin);
        Assert.False(ctx.IsGroupManager);
        Assert.Empty(ctx.Permissions);
    }

    [Fact]
    public void AppToken_WithAnExplicitRole_WithoutUserId_FallsBackToAppId()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app"), new Claim("building_os_role", "viewer")]);

        Assert.Equal("app", ctx!.UserId);
        Assert.Equal("viewer", ctx.Role);
    }

    // ---- legacy-admin — the pre-#667 behaviour, kept as a migration aid ----

    [Fact]
    public void Legacy_AppToken_ResolvesToAdmin()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app"), Sub("svc1")], Legacy);

        Assert.NotNull(ctx);
        Assert.Equal("admin", ctx!.Role);
        Assert.True(ctx.IsAdmin);
        Assert.Equal("svc1", ctx.UserId);
    }

    [Fact]
    public void Legacy_AppToken_WithoutUserId_FallsBackToAppId()
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app")], Legacy);

        Assert.NotNull(ctx);
        Assert.Equal("app", ctx!.UserId);
        Assert.Equal("admin", ctx.Role);
    }

    // ---- group-manager (#506) — the same under both policies ----

    /// <summary>
    /// #506: an application's client-credentials service account is given the group-manager role
    /// through the same building_os_role claim users get — under legacy-admin, the one opt-out from
    /// "app token = admin".
    /// </summary>
    [Theory]
    [InlineData(ClientCredentialsRolePolicy.Explicit)]
    [InlineData(ClientCredentialsRolePolicy.LegacyAdmin)]
    public void AppToken_CarryingGroupManager_IsAGroupManagerNotAnAdmin(ClientCredentialsRolePolicy policy)
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
            [new Claim("idtyp", "app"), Sub("svc-portal"), new Claim("building_os_role", "group-manager"),
             new Claim("permissions", "building:*:read")], policy);

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
    [Theory]
    [InlineData(ClientCredentialsRolePolicy.Explicit)]
    [InlineData(ClientCredentialsRolePolicy.LegacyAdmin)]
    public void AppToken_GroupManagerWithoutASubject_GetsNothing(ClientCredentialsRolePolicy policy)
    {
        var ctx = AuthorizationClaimResolver.TryResolve([new Claim("idtyp", "app"), new Claim("building_os_role", "group-manager")], policy);

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

    /// <summary>A Keycloak service-account token is not made an admin by being one, under either policy
    /// (legacy-admin elevates only idtyp=app).</summary>
    [Theory]
    [InlineData(ClientCredentialsRolePolicy.Explicit)]
    [InlineData(ClientCredentialsRolePolicy.LegacyAdmin)]
    public void KeycloakServiceAccountToken_KeepsItsOwnRole(ClientCredentialsRolePolicy policy)
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
            [Sub("svc-x"), new Claim("client_id", "x"), new Claim("building_os_role", "viewer")], policy);

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
    public void Legacy_AppToken_WithAnyOtherRoleClaim_StaysAdmin(string role)
    {
        var ctx = AuthorizationClaimResolver.TryResolve(
            [new Claim("idtyp", "app"), Sub("svc1"), new Claim("building_os_role", role)], Legacy);

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

    [Theory]
    [InlineData(null, ClientCredentialsRolePolicy.Explicit)]
    [InlineData("", ClientCredentialsRolePolicy.Explicit)]
    [InlineData("explicit", ClientCredentialsRolePolicy.Explicit)]
    [InlineData(" EXPLICIT ", ClientCredentialsRolePolicy.Explicit)]
    [InlineData("legacy-admin", ClientCredentialsRolePolicy.LegacyAdmin)]
    [InlineData(" Legacy-Admin ", ClientCredentialsRolePolicy.LegacyAdmin)]
    public void Policy_Parses(string? raw, ClientCredentialsRolePolicy expected)
    {
        Assert.Equal(expected, ClientCredentialsRoleOptions.ParsePolicy(raw, out var recognized));
        Assert.True(recognized);
    }

    /// <summary>#667: a typo must never re-open admin-by-default.</summary>
    [Theory]
    [InlineData("legacy")]
    [InlineData("admin")]
    [InlineData("legacy_admin")]
    public void Policy_UnknownValue_FallsBackToExplicit_AndSaysSo(string raw)
    {
        Assert.Equal(ClientCredentialsRolePolicy.Explicit, ClientCredentialsRoleOptions.ParsePolicy(raw, out var recognized));
        Assert.False(recognized);
    }
}
