using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Services;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.PointControl;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.PointControl;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Text.Json;

namespace BuildingOS.ApiServer.Test;

public class PointControllerTest
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static Point MakePoint(bool? writable = true) => new()
    {
        DtId = "urn:pt:1", Id = "PT001", Name = "SetTemp", Writable = writable
    };

    private static PointDetail Detail(Point point, Device? device = null)
        => new() { Point = point, Device = device };

    private static AuthorizationContext AdminAuth() => new()
    {
        UserId = "admin1", Role = "admin", Permissions = []
    };

    private static (PointController controller, Mock<IPointControlCommandPublisher> publisher)
        BuildController(
            PointDetail? detail,
            bool canWrite = true,
            Dictionary<string, string>? connectionTypeMap = null,
            string connectionTypeDefault = "hono",
            ControlSchema? schema = null)
    {
        var (controller, publisher, _) = BuildControllerWithResultBus(
            detail, canWrite, connectionTypeMap, connectionTypeDefault, schema);

        return (controller, publisher);
    }

    private static (PointController controller, Mock<IPointControlCommandPublisher> publisher, Mock<IControlResultBus> resultBus)
        BuildControllerWithResultBus(
            PointDetail? detail,
            bool canWrite = true,
            Dictionary<string, string>? connectionTypeMap = null,
            string connectionTypeDefault = "hono",
            ControlSchema? schema = null)
    {
        var twinView = new Mock<IAuthorizedTwinView>();
        twinView.Setup(v => v.CanWritePointAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(canWrite);
        twinView.Setup(v => v.GetPointDetailAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(detail != null
                    ? (TwinGetResult<PointDetail>)new TwinGetResult<PointDetail>.Ok(detail)
                    : new TwinGetResult<PointDetail>.NotFound());

        var resolver = new ControlTypeResolver(
            new ConfigGatewayConnectionRegistry(
                connectionTypeMap ?? new(), connectionTypeDefault,
                new Dictionary<string, IReadOnlyDictionary<string, string>>(),
                new Dictionary<string, IReadOnlyDictionary<string, string>>()));

        // Default: no schema → permissive (value validation skipped). Tests pass a schema to exercise it.
        var schemaResolver = new Mock<IControlSchemaResolver>();
        schemaResolver.Setup(r => r.ResolveAsync(It.IsAny<Point>(), It.IsAny<Device?>())).ReturnsAsync(schema);

        var publisher = new Mock<IPointControlCommandPublisher>();
        var resultBus = new Mock<IControlResultBus>();
        var repository = new Mock<IPointControlRepository>();

        var controller = new PointController(
            twinView.Object,
            resolver,
            schemaResolver.Object,
            resultBus.Object,
            publisher.Object,
            repository.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = BuildHttpContext(AdminAuth()),
        };

        return (controller, publisher, resultBus);
    }

    /// <summary>
    /// Builds a PointController wired for the ControlAudit (read history) tests: a stubbed
    /// GetPointAsync gate + an injectable audit repository. Returns the twinView mock so a test can
    /// override the read-authorization result (Ok/Forbidden/NotFound).
    /// </summary>
    private static (PointController controller, Mock<IPointControlRepository> repo, Mock<IAuthorizedTwinView> twinView)
        BuildAuditController(
            IReadOnlyList<PointControlAuditEntry>? entries = null,
            TwinGetResult<Point>? pointAccess = null)
    {
        var twinView = new Mock<IAuthorizedTwinView>();
        twinView.Setup(v => v.GetPointAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(pointAccess ?? new TwinGetResult<Point>.Ok(MakePoint()));

        var repo = new Mock<IPointControlRepository>();
        repo.Setup(r => r.ListAuditByPointAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries ?? []);

        var resolver = new ControlTypeResolver(
            new ConfigGatewayConnectionRegistry(
                new Dictionary<string, string>(), "hono",
                new Dictionary<string, IReadOnlyDictionary<string, string>>(),
                new Dictionary<string, IReadOnlyDictionary<string, string>>()));
        var schemaResolver = new Mock<IControlSchemaResolver>();
        var resultBus = new Mock<IControlResultBus>();
        var publisher = new Mock<IPointControlCommandPublisher>();

        var controller = new PointController(
            twinView.Object,
            resolver,
            schemaResolver.Object,
            resultBus.Object,
            publisher.Object,
            repo.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = BuildHttpContext(AdminAuth()) },
        };
        return (controller, repo, twinView);
    }

    private static PointControlAuditEntry AuditEntry(string pointId, string? resultJson, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        PointId = pointId,
        Request = """{"value":21.5}""",
        Result = resultJson,
        CreatedAt = createdAt,
        CompletedAt = resultJson is null ? null : createdAt.AddSeconds(1),
    };

    private static DefaultHttpContext BuildHttpContext(AuthorizationContext auth)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["AuthorizationContext"] = auth;
        return ctx;
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Control_Returns202_WithControlId_WhenPointIsWritable()
    {
        var (controller, publisher) = BuildController(Detail(MakePoint()));
        PointControlInfo? captured = null;
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .Callback<PointControlInfo, CancellationToken>((info, _) => captured = info)
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 21.5 }, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<PointController.ControlAcceptedResponse>(accepted.Value);
        Assert.NotNull(captured);
        Assert.NotEqual(Guid.Empty, captured!.id);
        Assert.Equal(captured.id, body.ControlId);
    }

    [Fact]
    public async Task Control_WaitsForResultSubscription_BeforePublishing()
    {
        var (controller, publisher, resultBus) = BuildControllerWithResultBus(Detail(MakePoint()));
        var prepareStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptionReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? preparedControlId = null;

        resultBus.Setup(b => b.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback<string, CancellationToken>((controlId, _) =>
                 {
                     preparedControlId = controlId;
                     prepareStarted.SetResult();
                 })
                 .Returns(subscriptionReady.Task);
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        var controlTask = controller.Control(
            "PT001", new PointController.PointControlRequest { Value = 1.0 }, CancellationToken.None);

        await prepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        publisher.Verify(
            p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);

        subscriptionReady.SetResult();
        var result = await controlTask;

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<PointController.ControlAcceptedResponse>(accepted.Value);
        Assert.Equal(body.ControlId.ToString(), preparedControlId);
        publisher.Verify(
            p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Control_UnsubscribesPreparedResult_WhenGatewayOffline()
    {
        var device = new Device { DtId = "d", Id = "D", Name = "g", GatewayId = "gw-sim" };
        var (controller, publisher, resultBus) = BuildControllerWithResultBus(
            Detail(MakePoint(), device), connectionTypeMap: new() { ["gw-sim"] = "bacnet-sim" });
        string? preparedControlId = null;

        resultBus.Setup(b => b.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback<string, CancellationToken>((controlId, _) => preparedControlId = controlId)
                 .Returns(Task.CompletedTask);
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ControlDeliveryStatus.GatewayOffline);

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 1.0 }, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        resultBus.Verify(b => b.UnsubscribeAsync(preparedControlId!), Times.Once);
    }

    [Fact]
    public async Task Control_UnsubscribesPreparedResult_WhenPublishingFails()
    {
        var (controller, publisher, resultBus) = BuildControllerWithResultBus(Detail(MakePoint()));
        string? preparedControlId = null;

        resultBus.Setup(b => b.PrepareAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback<string, CancellationToken>((controlId, _) => preparedControlId = controlId)
                 .Returns(Task.CompletedTask);
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("publish failed"));

        var result = await controller.Control(
            "PT001", new PointController.PointControlRequest { Value = 1.0 }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
        resultBus.Verify(b => b.UnsubscribeAsync(preparedControlId!), Times.Once);
    }

    [Fact]
    public async Task Control_SetsPointId_OnPublishedInfo()
    {
        var (controller, publisher) = BuildController(Detail(MakePoint()));
        PointControlInfo? captured = null;
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .Callback<PointControlInfo, CancellationToken>((info, _) => captured = info)
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        await controller.Control("PT001", new PointController.PointControlRequest { Value = 21.5 }, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("PT001", captured!.PointId);
    }

    [Fact]
    public async Task Control_SetsPointId_UrlDecoded()
    {
        var (controller, publisher) = BuildController(Detail(MakePoint()));
        PointControlInfo? captured = null;
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .Callback<PointControlInfo, CancellationToken>((info, _) => captured = info)
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        // pointId with URL-encoded slash
        await controller.Control("PT001%2Ftemp", new PointController.PointControlRequest { Value = 21.5 }, CancellationToken.None);

        Assert.Equal("PT001/temp", captured!.PointId);
    }

    [Fact]
    public async Task Control_DefaultsControlType_ToHono_ForUnmappedGateway()
    {
        // No-regression: an unmapped gateway resolves to Hono with the legacy { "value": v } body.
        var (controller, publisher) = BuildController(
            Detail(MakePoint(), new Device { DtId = "d", Id = "D", Name = "g", GatewayId = "unmapped" }));
        PointControlInfo? captured = null;
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .Callback<PointControlInfo, CancellationToken>((info, _) => captured = info)
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        await controller.Control("PT001", new PointController.PointControlRequest { Value = 21.5 }, CancellationToken.None);

        Assert.Equal(DeviceControlType.Hono, captured!.Type);
        using var doc = JsonDocument.Parse(captured.Body);
        Assert.Equal(21.5, doc.RootElement.GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Control_ResolvesBacnetSim_ForMappedGateway()
    {
        // point-id canonical (#181): the gateway resolves point_id → BACnet object/instance from the
        // shared point list, so the body carries only the value (no BACnet identity on the point).
        var device = new Device { DtId = "d", Id = "D", Name = "g", GatewayId = "gw-sim" };
        var (controller, publisher) = BuildController(
            Detail(MakePoint(), device), connectionTypeMap: new() { ["gw-sim"] = "bacnet-sim" });
        PointControlInfo? captured = null;
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .Callback<PointControlInfo, CancellationToken>((info, _) => captured = info)
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 23.0 }, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal(DeviceControlType.BacnetSim, captured!.Type);
        Assert.Equal("gw-sim", captured.GatewayId);
        using var doc = JsonDocument.Parse(captured.Body);
        Assert.Equal(23.0, doc.RootElement.GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Control_Returns400_WhenBacnetSimGatewayMissing()
    {
        // BacnetSim is delivered via the per-gateway bridge subject, so a gatewayId is mandatory —
        // a bacnet-sim point whose device has no GatewayId is not routable → not controllable.
        var device = new Device { DtId = "d", Id = "D", Name = "g", GatewayId = null };
        var (controller, publisher) = BuildController(
            Detail(MakePoint(), device), connectionTypeDefault: "bacnet-sim");

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 21.5 }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        publisher.Verify(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Control_Returns503_WhenGatewayOffline()
    {
        // #186: the per-gateway egress publish reports GatewayOffline (no live bridge replica) → the
        // controller fails fast with 503 instead of accepting a command that will silently time out.
        var device = new Device { DtId = "d", Id = "D", Name = "g", GatewayId = "gw-sim" };
        var (controller, publisher) = BuildController(
            Detail(MakePoint(), device), connectionTypeMap: new() { ["gw-sim"] = "bacnet-sim" });
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ControlDeliveryStatus.GatewayOffline);

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 23.0 }, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [Fact]
    public async Task Control_Returns400_WhenValueViolatesSchema()
    {
        // #153: a number-range schema rejects an out-of-range value before publishing.
        var (controller, publisher) = BuildController(
            Detail(MakePoint()),
            schema: new ControlSchema { DataType = "number", MinValue = 16, MaxValue = 30 });

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 99.0 }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        publisher.Verify(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Control_Accepts_WhenValueSatisfiesSchema()
    {
        var (controller, publisher) = BuildController(
            Detail(MakePoint()),
            schema: new ControlSchema { DataType = "number", MinValue = 16, MaxValue = 30 });
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 22.0 }, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public async Task Control_Returns400_WhenEnumValueNotAllowed()
    {
        var (controller, publisher) = BuildController(
            Detail(MakePoint()),
            schema: new ControlSchema { DataType = "enum", EnumLabels = """{"1":"cool","2":"heat"}""" });

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 5.0 }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        publisher.Verify(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Control_Accepts_WhenNoSchema_Permissive()
    {
        // Unschematized point → value validation skipped (backward compatible).
        var (controller, publisher) = BuildController(Detail(MakePoint()), schema: null);
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 123456.0 }, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public async Task Control_Returns403_WhenPointIsNotWritable()
    {
        var (controller, _) = BuildController(Detail(MakePoint(writable: false)), canWrite: false);

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 21.5 }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Control_Returns404_WhenPointDoesNotExist()
    {
        var (controller, _) = BuildController(detail: null);

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = 21.5 }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Control_Returns400_WhenValueIsMissing()
    {
        var (controller, publisher) = BuildController(Detail(MakePoint()));

        var result = await controller.Control("PT001", new PointController.PointControlRequest { Value = null }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        publisher.Verify(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── ControlAudit (制御監査履歴 read, #162) ───────────────────────────────

    [Fact]
    public async Task ControlAudit_ReturnsMappedEntries_WithNormalizedStatus()
    {
        var now = DateTime.UtcNow;
        var entries = new List<PointControlAuditEntry>
        {
            AuditEntry("PT001", """{"status":"success","response":"{}"}""", now),
            AuditEntry("PT001", """{"status":"failed","response":"timeout"}""", now.AddSeconds(-10)),
            AuditEntry("PT001", null, now.AddSeconds(-20)), // in-flight → pending
        };
        var (controller, _, _) = BuildAuditController(entries);

        var result = await controller.ControlAudit("PT001", 50, CancellationToken.None);

        var value = Assert.IsType<PointControlAuditResponse[]>(result.Value);
        Assert.Equal(3, value.Length);
        Assert.Equal("success", value[0].Status);
        Assert.Equal("failed", value[1].Status);
        Assert.Equal("pending", value[2].Status);
        Assert.Equal("""{"value":21.5}""", value[0].Request);
    }

    [Fact]
    public async Task ControlAudit_PassesDecodedPointIdAndCappedLimit_ToRepository()
    {
        var (controller, repo, _) = BuildAuditController();

        await controller.ControlAudit("PT001%2Ftemp", 999, CancellationToken.None);

        // limit is clamped to [1,200]; the pointId is URL-decoded before the query.
        repo.Verify(r => r.ListAuditByPointAsync("PT001/temp", 200, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ControlAudit_ClampsNonPositiveLimit_ToOne()
    {
        var (controller, repo, _) = BuildAuditController();

        await controller.ControlAudit("PT001", 0, CancellationToken.None);

        repo.Verify(r => r.ListAuditByPointAsync("PT001", 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ControlAudit_Returns403_WhenPointReadForbidden()
    {
        var (controller, repo, _) = BuildAuditController(pointAccess: new TwinGetResult<Point>.Forbidden());

        var result = await controller.ControlAudit("PT001", 50, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        repo.Verify(r => r.ListAuditByPointAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ControlAudit_Returns404_WhenPointNotFound()
    {
        var (controller, repo, _) = BuildAuditController(pointAccess: new TwinGetResult<Point>.NotFound());

        var result = await controller.ControlAudit("PT001", 50, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        repo.Verify(r => r.ListAuditByPointAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ControlAudit_ReturnsEmptyArray_WhenNoHistory()
    {
        var (controller, _, _) = BuildAuditController(entries: []);

        var result = await controller.ControlAudit("PT001", 50, CancellationToken.None);

        var value = Assert.IsType<PointControlAuditResponse[]>(result.Value);
        Assert.Empty(value);
    }
}
