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
        Build(string role = "viewer") => Build(role, out _);

    private static (MyResourcesController controller, Mock<IAuthorizationService> authz, Mock<IResourceIdMappingRepository> mapping)
        Build(string role, out Mock<IResourceDescendantResolver> descendants)
    {
        var authz = new Mock<IAuthorizationService>();
        authz.Setup(a => a.GetAccessibleResourcesAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccessibleResource>());
        authz.Setup(a => a.GetAccessibleResourceIdsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        var mapping = new Mock<IResourceIdMappingRepository>();
        mapping.Setup(m => m.ResolveOriginalIdsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        descendants = new Mock<IResourceDescendantResolver>();
        descendants.Setup(d => d.GetDescendantsAsync(It.IsAny<IReadOnlyCollection<(string, string)>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<string>>());
        var controller = new MyResourcesController(authz.Object, mapping.Object, descendants.Object)
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

    // ── expand=descendants (#509) ─────────────────────────────────────────────

    [Fact]
    public async Task Expand_AddsTheDescendantsOfTheResolvedGrants()
    {
        var (controller, authz, _) = Build("viewer", out var descendants);
        Spaces(authz, new AccessibleResource(H("R501"), "R501"), new AccessibleResource(H("R503"), null));
        descendants.Setup(d => d.GetDescendantsAsync(
                It.Is<IReadOnlyCollection<(string Type, string Id)>>(r => r.Count == 1 && r.Any(x => x.Item1 == "space" && x.Item2 == "R501")),
                "point", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<string>>
            {
                ["device"] = ["AHU-501"],
                ["point"] = ["PT-1", "PT-2"],
            });

        var ok = Assert.IsType<OkObjectResult>(
            await controller.GetMyResources(idFormat: "original", expand: "descendants", ct: default));
        var body = Assert.IsType<MyResourcesResponse>(ok.Value);

        Assert.Equal(["R501"], body.Resources!["space"]);
        Assert.Equal(["AHU-501"], body.Resources["device"]);
        Assert.Equal(["PT-1", "PT-2"], body.Resources["point"]);
        // An unresolved grant cannot be placed in the twin, so it is reported, not expanded.
        Assert.Equal([H("R503")], body.Unresolved!["space"]);
    }

    [Fact]
    public async Task Expand_PassesTheTargetType_AndKeepsDirectGrantsBelowIt()
    {
        var (controller, authz, _) = Build("viewer", out var descendants);
        Spaces(authz, new AccessibleResource(H("R501"), "R501"));
        authz.Setup(a => a.GetAccessibleResourcesAsync(It.IsAny<AuthorizationContext>(), "point", "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AccessibleResource(H("PT-9"), "PT-9")]);

        var ok = Assert.IsType<OkObjectResult>(
            await controller.GetMyResources(idFormat: "original", expand: "descendants", targetType: "space", ct: default));

        descendants.Verify(d => d.GetDescendantsAsync(
            It.IsAny<IReadOnlyCollection<(string, string)>>(), "space", It.IsAny<CancellationToken>()), Times.Once);
        // targetType bounds the expansion, not the grants: a direct point grant stays listed.
        Assert.Equal(["PT-9"], Assert.IsType<MyResourcesResponse>(ok.Value).Resources!["point"]);
    }

    [Fact]
    public async Task TargetType_WithoutExpand_IsIgnored()
    {
        var (controller, _, _) = Build();
        Assert.IsType<OkObjectResult>(await controller.GetMyResources(idFormat: "original", targetType: "gateway", ct: default));
    }

    [Fact]
    public async Task Expand_OverTheCap_Is422_WithAHint()
    {
        var (controller, authz, _) = Build("viewer", out var descendants);
        Spaces(authz, new AccessibleResource(H("R501"), "R501"));
        var many = Enumerable.Range(0, MyResourcesController.MaxExpandedIds + 1).Select(i => $"PT-{i}").ToList();
        descendants.Setup(d => d.GetDescendantsAsync(
                It.IsAny<IReadOnlyCollection<(string, string)>>(), "point", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<string>> { ["point"] = many });

        var result = await controller.GetMyResources(idFormat: "original", expand: "descendants", ct: default);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status.StatusCode);
    }

    [Theory]
    [InlineData(null, "descendants", null)]      // expand needs business ids
    [InlineData("hash", "descendants", null)]
    [InlineData("original", "children", null)]   // unknown expand
    [InlineData("original", "descendants", "gateway")] // unknown target type, with expand
    public async Task Expand_InvalidCombinations_Are400(string? idFormat, string? expand, string? targetType)
    {
        var (controller, _, _) = Build();
        Assert.IsType<BadRequestObjectResult>(
            await controller.GetMyResources(idFormat: idFormat, expand: expand, targetType: targetType, ct: default));
    }

    [Fact]
    public async Task Expand_ForAnAdmin_IsStillAll()
    {
        var (controller, _, _) = Build("admin", out var descendants);

        var ok = Assert.IsType<OkObjectResult>(
            await controller.GetMyResources(idFormat: "original", expand: "descendants", ct: default));

        Assert.Null(Assert.IsType<MyResourcesResponse>(ok.Value).Resources);
        descendants.VerifyNoOtherCalls();
    }
}
