using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.GatewayProvisioning;
using BuildingOs.ApiServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>Covers the point-list-projection plan's write-side triggers (Phase B): a point metadata
/// patch rebuilds just its owning gateway's cache row inline, while every other resource type falls
/// back to a coalesced background sweep (no cheap resource→gateway lookup exists for them yet).</summary>
public class ResourceMetadataControllerTest
{
    private static AuthorizationContext Auth() => new() { UserId = "actor", Role = "admin", Permissions = [] };

    private static (ResourceMetadataController controller, Mock<IDigitalTwinDatabase> db,
        Mock<IPointListMaterializer> materializer, Mock<IPointListMaterializerSweepTrigger> sweep)
        Build(Mock<IAuthorizedTwinView>? twinView = null)
    {
        var view = twinView ?? new Mock<IAuthorizedTwinView>();
        view.Setup(v => v.CanWriteResourceAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var db = new Mock<IDigitalTwinDatabase>();
        var materializer = new Mock<IPointListMaterializer>();
        var sweep = new Mock<IPointListMaterializerSweepTrigger>();

        var controller = new ResourceMetadataController(
            view.Object, db.Object, materializer.Object, sweep.Object,
            NullLogger<ResourceMetadataController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { [AuthorizationContextMiddleware.HttpContextKey] = Auth() },
                },
            },
        };
        return (controller, db, materializer, sweep);
    }

    [Fact]
    public async Task PatchPoint_RebuildsOwningGatewaysCache_WhenPointHasAGateway()
    {
        var (c, db, materializer, sweep) = Build();
        db.Setup(d => d.GetPoint("PT001"))
            .ReturnsAsync(new Point { DtId = "urn:dtid:pt1", Id = "PT001", Name = "PT001", GatewayName = "GW001" });

        var result = await c.PatchPoint("PT001", new ResourceMetadataPatchRequest(), default);

        Assert.IsType<NoContentResult>(result);
        materializer.Verify(m => m.RebuildGatewayAsync("GW001", It.IsAny<CancellationToken>()), Times.Once);
        sweep.Verify(s => s.RequestSweep(), Times.Never);
    }

    [Fact]
    public async Task PatchPoint_SkipsRebuild_WhenPointHasNoGateway()
    {
        var (c, db, materializer, _) = Build();
        db.Setup(d => d.GetPoint("PT001"))
            .ReturnsAsync(new Point { DtId = "urn:dtid:pt1", Id = "PT001", Name = "PT001" });

        var result = await c.PatchPoint("PT001", new ResourceMetadataPatchRequest(), default);

        Assert.IsType<NoContentResult>(result);
        materializer.Verify(m => m.RebuildGatewayAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PatchPoint_StillReturnsNoContent_WhenMaterializationThrows()
    {
        var (c, db, materializer, _) = Build();
        db.Setup(d => d.GetPoint("PT001"))
            .ReturnsAsync(new Point { DtId = "urn:dtid:pt1", Id = "PT001", Name = "PT001", GatewayName = "GW001" });
        materializer.Setup(m => m.RebuildGatewayAsync("GW001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db unavailable"));

        var result = await c.PatchPoint("PT001", new ResourceMetadataPatchRequest(), default);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task PatchPoint_ReturnsNotFound_WhenPointMissing()
    {
        var (c, db, materializer, _) = Build();
        db.Setup(d => d.GetPoint("PT001")).ReturnsAsync((Point?)null);

        var result = await c.PatchPoint("PT001", new ResourceMetadataPatchRequest(), default);

        Assert.IsType<NotFoundResult>(result);
        materializer.Verify(m => m.RebuildGatewayAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PatchBuilding_RequestsASweep_InsteadOfATargetedRebuild()
    {
        var (c, db, materializer, sweep) = Build();
        db.Setup(d => d.GetBuilding("urn:dtid:b1")).ReturnsAsync(new Building { DtId = "urn:dtid:b1", Id = "B1", Name = "B1" });

        var result = await c.PatchBuilding("urn:dtid:b1", new ResourceMetadataPatchRequest(), default);

        Assert.IsType<NoContentResult>(result);
        sweep.Verify(s => s.RequestSweep(), Times.Once);
        materializer.Verify(m => m.RebuildGatewayAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
