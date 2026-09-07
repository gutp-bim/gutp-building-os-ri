using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Microsoft.AspNetCore.Http;
using Moq;

namespace BuildingOS.ApiServer.Test;

public class PointDetailControllerTest
{
    private static AuthorizationContext AdminAuth() => new()
    {
        UserId = "admin1", Role = "admin", Permissions = []
    };

    private static AuthorizationContext NonAdminAuth() => new()
    {
        UserId = "user1", Role = "operator", Permissions = []
    };

    private static DefaultHttpContext BuildHttpContext(AuthorizationContext auth)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["AuthorizationContext"] = auth;
        return ctx;
    }

    // #413: [FromQuery] values are already fully URL-decoded (including %2F) by ASP.NET's query
    // binding before the action runs, unlike route segments (which leave %2F encoded). Re-applying
    // Uri.UnescapeDataString here corrupts a buildingDtId whose own text contains a literal
    // percent-escape (e.g. an SBCO hierarchical id encoded as building%3Asite%3A...).
    [Fact]
    public async Task List_PassesBuildingDtIdVerbatim_NotDoubleDecoded()
    {
        const string alreadyDecodedDtId = "https://example.org/resource/building%3Asite%3Asite-sim/bldg-sim";

        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListPointDetails(alreadyDecodedDtId)).ReturnsAsync([]);

        var controller = new PointDetailController(db.Object, Mock.Of<IControlSchemaResolver>(), Mock.Of<IAuthorizationService>())
        {
            ControllerContext = new() { HttpContext = BuildHttpContext(AdminAuth()) },
        };

        await controller.List(alreadyDecodedDtId, CancellationToken.None);

        db.Verify(d => d.ListPointDetails(alreadyDecodedDtId), Times.Once);
    }

    // Covers the non-admin branch: the fix touched the authorization check too
    // (authorizationService.CanAccessAsync), which the admin-path test above never exercises since
    // admins skip it entirely.
    [Fact]
    public async Task List_NonAdmin_ChecksAuthorizationWithBuildingDtIdVerbatim_NotDoubleDecoded()
    {
        const string alreadyDecodedDtId = "https://example.org/resource/building%3Asite%3Asite-sim/bldg-sim";

        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListPointDetails(alreadyDecodedDtId)).ReturnsAsync([]);

        var authorizationService = new Mock<IAuthorizationService>();
        authorizationService
            .Setup(a => a.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), "building", alreadyDecodedDtId, "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var controller = new PointDetailController(db.Object, Mock.Of<IControlSchemaResolver>(), authorizationService.Object)
        {
            ControllerContext = new() { HttpContext = BuildHttpContext(NonAdminAuth()) },
        };

        await controller.List(alreadyDecodedDtId, CancellationToken.None);

        authorizationService.Verify(a => a.CanAccessAsync(
            It.IsAny<AuthorizationContext>(), "building", alreadyDecodedDtId, "read", It.IsAny<CancellationToken>()),
            Times.Once);
        db.Verify(d => d.ListPointDetails(alreadyDecodedDtId), Times.Once);
    }
}
