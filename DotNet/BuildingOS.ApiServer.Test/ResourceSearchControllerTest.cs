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
}
