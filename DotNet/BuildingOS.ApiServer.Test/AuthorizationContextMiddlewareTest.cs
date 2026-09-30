using System.Security.Claims;
using BuildingOs.ApiServer.Middlewares;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.UserManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>The Admin-API fallback (a token without Building OS claims), #532.</summary>
public class AuthorizationContextMiddlewareTest
{
    private static async Task<AuthorizationContext> ResolveAsync(Mock<IUserManagementService> svc, IMemoryCache cache)
    {
        var services = new ServiceCollection().AddSingleton(svc.Object).BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            // A token with a subject but no building_os_role / permissions claims.
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u1")], "Bearer")),
        };
        var middleware = new AuthorizationContextMiddleware(
            _ => Task.CompletedTask, NullLogger<AuthorizationContextMiddleware>.Instance);

        await middleware.InvokeAsync(http, cache);

        return (AuthorizationContext)http.Items[AuthorizationContextMiddleware.HttpContextKey]!;
    }

    private static Mock<IUserManagementService> Service(EntraUser user)
    {
        var svc = new Mock<IUserManagementService>();
        svc.Setup(s => s.GetUserByIdAsync("u1", It.IsAny<CancellationToken>())).ReturnsAsync(user);
        return svc;
    }

    [Fact]
    public async Task ResolvedUser_UsesTheRole_AndIsCached()
    {
        var svc = Service(new EntraUser { Id = "u1", DisplayName = "u1", Role = "operator", Permissions = ["floor:1:read"] });
        var cache = new MemoryCache(new MemoryCacheOptions());

        var ctx = await ResolveAsync(svc, cache);
        await ResolveAsync(svc, cache);

        Assert.Equal("operator", ctx.Role);
        Assert.Equal(["floor:1:read"], ctx.Permissions);
        svc.Verify(s => s.GetUserByIdAsync("u1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GroupRoleUnresolved_FallsBackToTheUsersOwnAttributes_InsteadOfDowngrading()
    {
        // The group lookup failed (403 / 5xx): the user's own role and permissions still apply.
        var svc = Service(new EntraUser
        {
            Id = "u1", DisplayName = "u1", Role = null, GroupRoleUnresolved = true,
            OwnAttributeRole = "operator", Permissions = ["floor:1:read"],
        });

        var ctx = await ResolveAsync(svc, new MemoryCache(new MemoryCacheOptions()));

        Assert.Equal("operator", ctx.Role);
        Assert.Equal(["floor:1:read"], ctx.Permissions);
    }

    [Fact]
    public async Task GroupRoleUnresolved_IsNotCached_SoTheNextRequestRetriesTheGroups()
    {
        var svc = Service(new EntraUser
        {
            Id = "u1", DisplayName = "u1", GroupRoleUnresolved = true, Permissions = ["floor:1:read"],
        });
        var cache = new MemoryCache(new MemoryCacheOptions());

        var ctx = await ResolveAsync(svc, cache);
        await ResolveAsync(svc, cache);

        Assert.Equal("user", ctx.Role);
        Assert.Empty(ctx.Permissions);
        svc.Verify(s => s.GetUserByIdAsync("u1", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GroupRoleUnresolved_WithoutAnOwnRole_FailsClosed_EvenWithALegacyAdminRole()
    {
        // No own role, a viewer group that could not be read, a leftover legacy buildingos_role=admin: the
        // legacy value (which the group role would hide) must not make the user an admin — fail closed to
        // role=user with no permissions, as before #532.
        var svc = Service(new EntraUser
        {
            Id = "u1", DisplayName = "u1", Role = null, GroupRoleUnresolved = true,
            OwnAttributeRole = null, Permissions = ["building:1:write"],
        });

        var ctx = await ResolveAsync(svc, new MemoryCache(new MemoryCacheOptions()));

        Assert.Equal("user", ctx.Role);
        Assert.False(ctx.IsAdmin);
        Assert.Empty(ctx.Permissions);
    }
}
