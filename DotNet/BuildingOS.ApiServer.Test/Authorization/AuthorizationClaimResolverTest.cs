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
