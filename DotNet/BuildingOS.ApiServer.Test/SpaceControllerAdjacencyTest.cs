using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// GET /spaces/{spaceDtId}/adjacent-spaces (#440) — result mapping and dtId unescaping. The
/// authorization itself lives in AuthorizedTwinView (AuthorizedTwinViewAdjacencyTest); the
/// controller only translates the three outcomes.
/// </summary>
public class SpaceControllerAdjacencyTest
{
    private static (SpaceController c, Mock<IAuthorizedTwinView> view) Build(TwinGetResult<Space[]> result)
    {
        var view = new Mock<IAuthorizedTwinView>();
        view.Setup(v => v.ListAdjacentSpacesAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        var controller = new SpaceController(view.Object)
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
    public async Task GetAdjacentSpaces_Ok_ReturnsSpaces()
    {
        var neighbour = new Space { DtId = "urn:test:room-b", Id = "ROOM-B", Name = "Room B" };
        var (c, _) = Build(new TwinGetResult<Space[]>.Ok([neighbour]));

        var result = await c.GetAdjacentSpaces("urn:test:room-a", default);

        Assert.Equal("urn:test:room-b", Assert.Single(result.Value!).DtId);
    }

    [Fact]
    public async Task GetAdjacentSpaces_Forbidden_ReturnsForbid()
    {
        var (c, _) = Build(new TwinGetResult<Space[]>.Forbidden());
        var result = await c.GetAdjacentSpaces("urn:test:room-a", default);
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetAdjacentSpaces_NotFound_ReturnsNotFound()
    {
        var (c, _) = Build(new TwinGetResult<Space[]>.NotFound());
        var result = await c.GetAdjacentSpaces("urn:test:room-a", default);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetAdjacentSpaces_UnescapesPercentEncodedDtId()
    {
        // DtIds are the node URIs themselves (https://www.sbco.or.jp/ont/resource/... in
        // sbco-sample.ttl), so a client sends them percent-encoded exactly as GET /spaces/{id} does.
        var (c, view) = Build(new TwinGetResult<Space[]>.Ok([]));

        await c.GetAdjacentSpaces("https%3A%2F%2Fwww.sbco.or.jp%2Font%2Fresource%2Froom-201", default);

        view.Verify(v => v.ListAdjacentSpacesAsync(
            It.IsAny<AuthorizationContext>(),
            "https://www.sbco.or.jp/ont/resource/room-201",
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
