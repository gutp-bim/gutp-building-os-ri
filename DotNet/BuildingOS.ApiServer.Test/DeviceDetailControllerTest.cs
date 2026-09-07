using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Microsoft.AspNetCore.Http;
using Moq;

namespace BuildingOS.ApiServer.Test;

public class DeviceDetailControllerTest
{
    private static AuthorizationContext AdminAuth() => new()
    {
        UserId = "admin1", Role = "admin", Permissions = []
    };

    private static DefaultHttpContext BuildHttpContext(AuthorizationContext auth)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["AuthorizationContext"] = auth;
        return ctx;
    }

    // #413: see PointDetailControllerTest.List_PassesBuildingDtIdVerbatim_NotDoubleDecoded for why
    // [FromQuery] values must not be re-decoded.
    [Fact]
    public async Task List_PassesBuildingDtIdVerbatim_NotDoubleDecoded()
    {
        const string alreadyDecodedDtId = "https://example.org/resource/building%3Asite%3Asite-sim/bldg-sim";

        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListDeviceDetails(alreadyDecodedDtId)).ReturnsAsync([]);

        var controller = new DeviceDetailController(db.Object, Mock.Of<IAuthorizationService>())
        {
            ControllerContext = new() { HttpContext = BuildHttpContext(AdminAuth()) },
        };

        await controller.List(alreadyDecodedDtId, CancellationToken.None);

        db.Verify(d => d.ListDeviceDetails(alreadyDecodedDtId), Times.Once);
    }
}
