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

    // Covers the non-admin branch: the fix touched the authorization check too
    // (authorizationService.CanAccessAsync), which the admin-path test above never exercises since
    // admins skip it entirely.
    [Fact]
    public async Task List_NonAdmin_ChecksAuthorizationWithBuildingDtIdVerbatim_NotDoubleDecoded()
    {
        const string alreadyDecodedDtId = "https://example.org/resource/building%3Asite%3Asite-sim/bldg-sim";

        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListDeviceDetails(alreadyDecodedDtId)).ReturnsAsync([]);

        var authorizationService = new Mock<IAuthorizationService>();
        authorizationService
            .Setup(a => a.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), "building", alreadyDecodedDtId, "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var controller = new DeviceDetailController(db.Object, authorizationService.Object)
        {
            ControllerContext = new() { HttpContext = BuildHttpContext(NonAdminAuth()) },
        };

        await controller.List(alreadyDecodedDtId, CancellationToken.None);

        authorizationService.Verify(a => a.CanAccessAsync(
            It.IsAny<AuthorizationContext>(), "building", alreadyDecodedDtId, "read", It.IsAny<CancellationToken>()),
            Times.Once);
        db.Verify(d => d.ListDeviceDetails(alreadyDecodedDtId), Times.Once);
    }

    // Review of #516: the building is authorized by its business id (#504), like the tree reads.
    [Fact]
    public async Task List_NonAdmin_BuildingGrantOnTheBusinessId_IsOk()
    {
        const string dtId = "https://www.sbco.or.jp/ont/resource/B1";
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.GetBuilding(dtId)).ReturnsAsync(new Building { DtId = dtId, Id = "B1", Name = "B1" });
        db.Setup(d => d.ListDeviceDetails(dtId)).ReturnsAsync([]);
        var authorizationService = new Mock<IAuthorizationService>();
        authorizationService
            .Setup(a => a.CanAccessAsync(It.IsAny<AuthorizationContext>(), "building", "B1", "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = new DeviceDetailController(db.Object, authorizationService.Object)
        {
            ControllerContext = new() { HttpContext = BuildHttpContext(NonAdminAuth()) },
        };

        var result = await controller.List(dtId, CancellationToken.None);

        Assert.IsNotType<Microsoft.AspNetCore.Mvc.ForbidResult>(result.Result);
        db.Verify(d => d.ListDeviceDetails(dtId), Times.Once);
    }
}
