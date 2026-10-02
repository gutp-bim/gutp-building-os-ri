using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BuildingOS.ApiServer.Test;

public class ResourceSearchControllerTest
{
    private static (ResourceSearchController c, Mock<IAuthorizedTwinView> view) Build()
    {
        var view = new Mock<IAuthorizedTwinView>();
        view.Setup(v => v.SearchAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ResourceSearchHit>());
        var controller = new ResourceSearchController(view.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = "u1", Role = "admin", Permissions = [] } },
                },
            },
        };
        return (controller, view);
    }

    [Fact]
    public async Task Search_ForwardsTags_FilteringBlanks()
    {
        var (c, view) = Build();

        await c.Search(q: "temp", type: "point", buildingId: null, tag: ["hvac", "", "  ", "temperature"], limit: 50, offset: 0, ct: default);

        view.Verify(v => v.SearchAsync(
            It.IsAny<AuthorizationContext>(), "temp", "point", null,
            It.Is<IReadOnlyList<string>>(t => t.Count == 2 && t[0] == "hvac" && t[1] == "temperature"),
            50, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Search_NoTagParam_PassesEmptyTags()
    {
        var (c, view) = Build();

        await c.Search(q: "x", type: null, buildingId: null, tag: null, limit: 50, offset: 0, ct: default);

        view.Verify(v => v.SearchAsync(
            It.IsAny<AuthorizationContext>(), "x", null, null,
            It.Is<IReadOnlyList<string>>(t => t.Count == 0),
            50, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Search_NegativeOffset_Returns400()
    {
        var (c, _) = Build();
        var result = await c.Search(q: null, type: null, buildingId: null, tag: null, limit: 50, offset: -1, ct: default);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Tags_ForwardsPrefixAndClampsLimit()
    {
        var (c, view) = Build();
        view.Setup(v => v.ListTagsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ResourceTagCount>());

        await c.Tags(prefix: " tem ", limit: 9999, ct: default);

        view.Verify(v => v.ListTagsAsync(It.IsAny<AuthorizationContext>(), "tem", 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Search_WithAttributeFilters_UsesTheFilteredPath()
    {
        var (c, view) = Build();
        view.Setup(v => v.SearchFilteredAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<ResourceAttributeFilter>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ResourceSearchHit>());

        await c.Search(q: null, type: "point", buildingId: null, tag: null, limit: 50, offset: 0,
            deviceType: ["AHU", " "], pointType: null, unit: ["degC"], gatewayId: null, ct: default);

        view.Verify(v => v.SearchFilteredAsync(
            It.IsAny<AuthorizationContext>(), null, "point", null, It.IsAny<IReadOnlyList<string>>(),
            It.Is<ResourceAttributeFilter>(a => a.DeviceTypes.SequenceEqual(new[] { "AHU" }) && a.Units.SequenceEqual(new[] { "degC" })),
            50, 0, It.IsAny<CancellationToken>()), Times.Once);
        view.Verify(v => v.SearchAsync(
            It.IsAny<AuthorizationContext>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Facets_ForwardsTheSameFiltersAsSearch()
    {
        var (c, view) = Build();
        view.Setup(v => v.GetFacetsAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<ResourceAttributeFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceFacets());

        await c.Facets(q: "x", type: "point", buildingId: "urn:b", tag: ["hvac", ""], deviceType: ["VAV"],
            pointType: null, unit: null, gatewayId: ["GW-1"], ct: default);

        view.Verify(v => v.GetFacetsAsync(
            It.IsAny<AuthorizationContext>(), "x", "point", "urn:b",
            It.Is<IReadOnlyList<string>>(t => t.SequenceEqual(new[] { "hvac" })),
            It.Is<ResourceAttributeFilter>(a => a.DeviceTypes.SequenceEqual(new[] { "VAV" }) && a.GatewayIds.SequenceEqual(new[] { "GW-1" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
