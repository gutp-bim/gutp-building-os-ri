using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared.Domain.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// #504 B: <c>GET /api/v1/my-resources?idFormat=original</c> returns the ids a client can use
/// (business ids) and lists the grants whose original id cannot be recovered separately, instead of
/// mixing raw hashes in. The default (<c>idFormat</c> absent or <c>hash</c>) is unchanged (ADR-0008:
/// v1 is additive-only).
/// </summary>
public class MyResourcesControllerTest
{
    private static string H(string id) => PermissionHelper.HashResourceId(id);

    private static (MyResourcesController controller, Mock<IAuthorizationService> authz, Mock<IResourceIdMappingRepository> mapping)
        Build(string role = "viewer")
    {
        var authz = new Mock<IAuthorizationService>();
        authz.Setup(a => a.GetAccessibleResourcesAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccessibleResource>());
        authz.Setup(a => a.GetAccessibleResourceIdsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        var mapping = new Mock<IResourceIdMappingRepository>();
        mapping.Setup(m => m.ResolveOriginalIdsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        var controller = new MyResourcesController(authz.Object, mapping.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = "u1", Role = role, Permissions = [] } },
                },
            },
        };
        return (controller, authz, mapping);
    }

    private static void Spaces(Mock<IAuthorizationService> authz, params AccessibleResource[] resources)
    {
        authz.Setup(a => a.GetAccessibleResourcesAsync(It.IsAny<AuthorizationContext>(), "space", "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(resources);
        authz.Setup(a => a.GetAccessibleResourceIdsAsync(It.IsAny<AuthorizationContext>(), "space", "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(resources.Select(r => r.Hash).ToArray());
    }

    [Fact]
    public async Task Default_IsUnchanged_HashesWithoutAMappingStayHashes()
    {
        var (controller, authz, _) = Build();
        Spaces(authz, new AccessibleResource(H("R501"), "R501"));

        var ok = Assert.IsType<OkObjectResult>(await controller.GetMyResources(ct: default));
        var body = Assert.IsType<MyResourcesResponse>(ok.Value);

        Assert.Equal([H("R501")], body.Resources!["space"]);
        Assert.Null(body.Unresolved);
    }

    [Fact]
    public async Task Original_ReturnsBusinessIds_FromGroupsAndTheMapping_AndSeparatesTheRest()
    {
        var (controller, authz, mapping) = Build();
        Spaces(authz,
            new AccessibleResource(H("R501"), "R501"),   // via a Group: original known
            new AccessibleResource(H("R502"), null),     // direct, recorded in the mapping table
            new AccessibleResource(H("R503"), null));    // direct, never recorded
        mapping.Setup(m => m.ResolveOriginalIdsAsync(
                It.Is<IReadOnlyList<string>>(l => l.Contains(H("R502"))), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { [H("R502")] = "R502" });

        var ok = Assert.IsType<OkObjectResult>(await controller.GetMyResources(idFormat: "original", ct: default));
        var body = Assert.IsType<MyResourcesResponse>(ok.Value);

        Assert.Equal(["R501", "R502"], body.Resources!["space"]);
        Assert.Equal([H("R503")], body.Unresolved!["space"]);
        Assert.Empty(body.Unresolved["building"]);
    }

    [Fact]
    public async Task Original_ForAnAdmin_IsStillAll()
    {
        var (controller, _, _) = Build(role: "admin");

        var ok = Assert.IsType<OkObjectResult>(await controller.GetMyResources(idFormat: "original", ct: default));
        var body = Assert.IsType<MyResourcesResponse>(ok.Value);

        Assert.True(body.IsAdmin);
        Assert.Null(body.Resources);
    }

    [Theory]
    [InlineData("ORIGINAL")]
    [InlineData("hash")]
    public async Task IdFormat_IsCaseInsensitive_AndHashIsTheDefault(string idFormat)
    {
        var (controller, _, _) = Build();
        Assert.IsType<OkObjectResult>(await controller.GetMyResources(idFormat: idFormat, ct: default));
    }

    [Fact]
    public async Task IdFormat_Unknown_Is400()
    {
        var (controller, _, _) = Build();
        Assert.IsType<BadRequestObjectResult>(await controller.GetMyResources(idFormat: "dtid", ct: default));
    }

    [Fact]
    public async Task Accessible_Original_ReturnsBusinessIds_AndUnresolved()
    {
        var (controller, authz, _) = Build();
        Spaces(authz, new AccessibleResource(H("R501"), "R501"), new AccessibleResource(H("R503"), null));

        var ok = Assert.IsType<OkObjectResult>(
            await controller.GetAccessible("space", "read", idFormat: "original", ct: default));
        var body = Assert.IsType<AccessibleResourcesResponse>(ok.Value);

        Assert.Equal(["R501"], body.AccessibleResourceIds);
        Assert.Equal([H("R503")], body.UnresolvedResourceIds);
    }
}
