using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BuildingOS.ApiServer.Test;

// #413: GET /point-details?buildingDtId= double-decoded an already-URL-decoded [FromQuery] value
// (ASP.NET model binding decodes once; the controller then called Uri.UnescapeDataString again),
// mangling dtIds whose business id itself contains literal "%3A"/"%2F" (SBCO-standard hierarchical
// ids percent-encode ":" and "/" as part of the id text, e.g. "building%3Asite%3Asite-sim%2Fbldg-sim").
// These tests pin the buildingDtId to reach both the authorization check and the twin query
// byte-for-byte as bound, with no extra decode.
public class PointDetailControllerTest
{
    private const string EncodedBuildingDtId =
        "https://www.sbco.or.jp/ont/resource/building%3Asite%3Asite-sim%2Fbldg-sim";

    private static AuthorizationContext NonAdminAuth() => new()
    {
        UserId = "u1", Role = "operator", Permissions = []
    };

    private static (PointDetailController controller, Mock<IDigitalTwinDatabase> db, Mock<BuildingOS.Shared.Domain.Authorization.IAuthorizationService> authz)
        BuildController(AuthorizationContext? auth = null)
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListPointDetails(It.IsAny<string>())).ReturnsAsync([]);

        var authz = new Mock<BuildingOS.Shared.Domain.Authorization.IAuthorizationService>();
        authz.Setup(a => a.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        var schemaResolver = new Mock<IControlSchemaResolver>();

        var controller = new PointDetailController(db.Object, schemaResolver.Object, authz.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = BuildHttpContext(auth ?? NonAdminAuth()),
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

        db.Verify(d => d.ListPointDetails(EncodedBuildingDtId), Times.Once);
    }

    [Fact]
    public async Task List_Returns404_WhenTwinReportsBuildingNotFound()
    {
        var (controller, db, _) = BuildController();
        db.Setup(d => d.ListPointDetails(It.IsAny<string>()))
          .ThrowsAsync(new DigitalTwinNotFoundException());

        var result = await controller.List(EncodedBuildingDtId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
