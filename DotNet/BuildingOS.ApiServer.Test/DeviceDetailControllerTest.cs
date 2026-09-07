using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BuildingOS.ApiServer.Test;

// #413: same double-decode bug as PointDetailController — GET /device-details?buildingDtId= called
// Uri.UnescapeDataString on an already-URL-decoded [FromQuery] value.
public class DeviceDetailControllerTest
{
    private const string EncodedBuildingDtId =
        "https://www.sbco.or.jp/ont/resource/building%3Asite%3Asite-sim%2Fbldg-sim";

    private static AuthorizationContext NonAdminAuth() => new()
    {
        UserId = "u1", Role = "operator", Permissions = []
    };

    private static (DeviceDetailController controller, Mock<IDigitalTwinDatabase> db, Mock<BuildingOS.Shared.Domain.Authorization.IAuthorizationService> authz)
        BuildController()
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListDeviceDetails(It.IsAny<string>())).ReturnsAsync([]);

        var authz = new Mock<BuildingOS.Shared.Domain.Authorization.IAuthorizationService>();
        authz.Setup(a => a.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        var controller = new DeviceDetailController(db.Object, authz.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = BuildHttpContext(NonAdminAuth()),
            },
        };

        return (controller, db, authz);
    }

    private static DefaultHttpContext BuildHttpContext(AuthorizationContext auth)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["AuthorizationContext"] = auth;
        return ctx;
    }

    [Fact]
    public async Task List_PassesBuildingDtId_ToAuthorizationCheck_WithoutExtraDecoding()
    {
        var (controller, _, authz) = BuildController();

        await controller.List(EncodedBuildingDtId, CancellationToken.None);

        authz.Verify(a => a.CanAccessAsync(
            It.IsAny<AuthorizationContext>(), "building", EncodedBuildingDtId, "read",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_PassesBuildingDtId_ToTwinQuery_WithoutExtraDecoding()
    {
        var (controller, db, _) = BuildController();

        await controller.List(EncodedBuildingDtId, CancellationToken.None);

        db.Verify(d => d.ListDeviceDetails(EncodedBuildingDtId), Times.Once);
    }
}
