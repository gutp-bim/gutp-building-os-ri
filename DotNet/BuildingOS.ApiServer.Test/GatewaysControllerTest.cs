using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

public class GatewaysControllerTest
{
    private static AuthorizationContext Auth(string role) =>
        new() { UserId = "actor", Role = role, Permissions = [] };

    private static (GatewaysController c, Mock<IDigitalTwinDatabase> twin,
        Mock<IPointListUpdatePublisher> pub, Mock<IAdminAuditRecorder> audit,
        Mock<ITelemetryQueryRouter> telemetry)
        Build(AuthorizationContext auth, string[]? gatewayIds = null)
    {
        var twin = new Mock<IDigitalTwinDatabase>();
        twin.Setup(t => t.ListGatewayIds()).ReturnsAsync(gatewayIds ?? []);
        twin.Setup(t => t.ListGatewayPointList(It.IsAny<string>())).ReturnsAsync([]);

        var registry = new Mock<IGatewayConnectionRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns<string?>(id => new GatewayConnection(
                id ?? "", "hono",
                new Dictionary<string, string> { ["host"] = "h", ["password"] = "p" }));

        var pub = new Mock<IPointListUpdatePublisher>();
        var audit = new Mock<IAdminAuditRecorder>();

        var telemetry = new Mock<ITelemetryQueryRouter>();
        // Default: no telemetry for any point (→ LastTelemetryAt null).
        telemetry.Setup(t => t.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var controller = new GatewaysController(
            twin.Object, registry.Object, pub.Object, audit.Object, telemetry.Object,
            NullLogger<GatewaysController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Items = { ["AuthorizationContext"] = auth } },
            },
        };
        return (controller, twin, pub, audit, telemetry);
    }

    [Fact]
    public async Task List_NonAdmin_IsForbidden()
    {
        var (c, _, _, _, _) = Build(Auth("operator"));
        Assert.IsType<ForbidResult>(await c.List(default));
    }

    [Fact]
    public async Task List_MasksSecretSettings()
    {
        var (c, _, _, _, _) = Build(Auth("admin"), ["GW001"]);
        var ok = Assert.IsType<OkObjectResult>(await c.List(default));
        var views = Assert.IsAssignableFrom<IReadOnlyList<GatewayAdminView>>(ok.Value);
        var gw = Assert.Single(views);
        Assert.Equal("GW001", gw.GatewayId);
        Assert.Equal("hono", gw.BindingType);
        Assert.Equal("h", gw.Settings["host"]);
        Assert.Equal("***", gw.Settings["password"]);
    }

    [Fact]
    public async Task Get_UnknownGateway_Returns404()
    {
        var (c, _, _, _, _) = Build(Auth("admin"), ["GW001"]);
        Assert.IsType<NotFoundResult>(await c.Get("GHOST", default));
    }

    [Fact]
    public async Task ResyncPointList_UnknownGateway_Returns404()
    {
        var (c, _, pub, _, _) = Build(Auth("admin"), ["GW001"]);
        Assert.IsType<NotFoundResult>(await c.ResyncPointList("GHOST", default));
        pub.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResyncPointList_Publishes_AndAudits()
    {
        var (c, _, pub, audit, _) = Build(Auth("admin"), ["GW001"]);

        var result = await c.ResyncPointList("GW001", default);

        Assert.IsType<AcceptedResult>(result);
        pub.Verify(p => p.PublishAsync("GW001", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r =>
                r.SubjectType == "gateway" && r.Action == "resync-pointlist" && r.Result == AdminAuditResult.Success),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_LastTelemetryAt_IsNull_WhenNoPointsHaveReported()
    {
        // Default telemetry mock returns no samples → derived last-seen is null (#181 Phase 2).
        var (c, twin, _, _, _) = Build(Auth("admin"), ["GW001"]);
        twin.Setup(t => t.ListGatewayPointList("GW001")).ReturnsAsync(
        [
            new GatewayPointEntry { PointId = "p1" },
            new GatewayPointEntry { PointId = "p2" },
        ]);

        var ok = Assert.IsType<OkObjectResult>(await c.List(default));
        var gw = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<GatewayAdminView>>(ok.Value));
        Assert.Null(gw.LastTelemetryAt);
    }

    [Fact]
    public async Task List_LastTelemetryAt_IsTheMaxTimestampAcrossThePoints()
    {
        // p1 reported at 00:00, p2 at 00:05 → last-seen is the newer of the two.
        var (c, twin, _, _, telemetry) = Build(Auth("admin"), ["GW001"]);
        twin.Setup(t => t.ListGatewayPointList("GW001")).ReturnsAsync(
        [
            new GatewayPointEntry { PointId = "p1" },
            new GatewayPointEntry { PointId = "p2" },
        ]);
        telemetry.Setup(t => t.QueryAsync(
                It.Is<TelemetryQueryRequest>(r => r.PointId == "p1" && r.Latest), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ValidTelemetryData { Datetime = "2026-07-18T00:00:00Z", Value = 1 }]);
        telemetry.Setup(t => t.QueryAsync(
                It.Is<TelemetryQueryRequest>(r => r.PointId == "p2" && r.Latest), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ValidTelemetryData { Datetime = "2026-07-18T00:05:00Z", Value = 2 }]);

        var ok = Assert.IsType<OkObjectResult>(await c.List(default));
        var gw = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<GatewayAdminView>>(ok.Value));
        Assert.NotNull(gw.LastTelemetryAt);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-18T00:05:00Z"),
            DateTimeOffset.Parse(gw.LastTelemetryAt!));
    }
}
