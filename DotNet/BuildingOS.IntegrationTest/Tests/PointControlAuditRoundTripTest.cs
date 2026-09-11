using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.PointControl;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.PointControl;
using BuildingOS.Shared.Infrastructure.PointControlAudit;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// The DB round-trip #177 deferred and #333 was caused by: POST /points/{id}/control must leave a row
/// in point_control_audit that GET /points/{id}/control-audit reads back. The unit tests mock the
/// repository, so only this test proves the write actually reaches PostgreSQL and surfaces in the API
/// the shipped 制御履歴 UI calls.
/// </summary>
[Collection(Names.Postgres)]
public class PointControlAuditRoundTripTest(PostgresFixture postgres) : IntegrationTestBase
{
    [Fact]
    public async Task Control_ThenControlAudit_ReturnsTheDispatchedCommand_WithRealPostgres()
    {
        var pointId = $"PT-audit-{Guid.NewGuid():N}";
        await using var services = CreateServices();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RelationalDbContext>().Database.MigrateAsync();

        var auditWriter = AuditWriter(services);
        var controller = BuildController(scope, auditWriter, pointId, out var publisher);
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        var accepted = Assert.IsType<AcceptedResult>(await controller.Control(
            pointId, new PointController.PointControlRequest { Value = 21.5 }, CancellationToken.None));
        var controlId = Assert.IsType<PointController.ControlAcceptedResponse>(accepted.Value).ControlId;

        var audit = await controller.ControlAudit(pointId, limit: 10, CancellationToken.None);
        var entries = Assert.IsType<PointControlAuditResponse[]>(audit.Value);

        var entry = Assert.Single(entries);
        Assert.Equal(controlId, entry.ControlId);
        Assert.Equal(pointId, entry.PointId);
        // No result has been published yet, so the command is still in flight.
        Assert.Equal("pending", entry.Status);
        Assert.Null(entry.CompletedAt);
        // #461: the authenticated principal must survive the real actor_sub column, not just the
        // in-memory mapping — this is the only test that runs the migration against PostgreSQL.
        Assert.Equal("admin1", entry.ActorSub);
        Assert.Null(entry.ActorName);
    }

    [Fact]
    public async Task ControlResult_IsPersisted_AndVisibleInTheAuditHistory()
    {
        var pointId = $"PT-audit-{Guid.NewGuid():N}";
        await using var services = CreateServices();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RelationalDbContext>().Database.MigrateAsync();

        var auditWriter = AuditWriter(services);
        var controller = BuildController(scope, auditWriter, pointId, out var publisher);
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        var accepted = Assert.IsType<AcceptedResult>(await controller.Control(
            pointId, new PointController.PointControlRequest { Value = 21.5 }, CancellationToken.None));
        var controlId = Assert.IsType<PointController.ControlAcceptedResponse>(accepted.Value).ControlId;

        // Stand in for the result arriving on building-os.control.result.{controlId}: this is the
        // exact call ControlAuditResultSubscriber makes when either dispatch path reports back.
        await auditWriter.RecordResultAsync(
            controlId.ToString(), success: true, response: "{\"ok\":true}", CancellationToken.None);

        var audit = await controller.ControlAudit(pointId, limit: 10, CancellationToken.None);
        var entry = Assert.Single(Assert.IsType<PointControlAuditResponse[]>(audit.Value));

        Assert.Equal(controlId, entry.ControlId);
        Assert.Equal("success", entry.Status);
        Assert.NotNull(entry.CompletedAt);
    }

    /// <summary>
    /// #418 investigation: the point-id decode ahead of the repository. Both Control (write —
    /// PointControlInfo.PointId) and ControlAudit (read — ListAuditByPointAsync) apply the identical
    /// <c>Uri.UnescapeDataString(pointId)</c> to the route value before it reaches the repository, so
    /// a point id containing characters a client must percent-encode (space, '+', '#', non-ASCII) is
    /// expected to round-trip identically on both sides — proving there is no write/read decode
    /// mismatch to fix here (ASP.NET Core routing itself decodes normal %XX pairs once before binding
    /// the route value; this test feeds the still-percent-encoded form both actions receive).
    /// </summary>
    [Fact]
    public async Task Control_ThenControlAudit_WithUrlEncodableCharacters_DecodeIdentically()
    {
        var decodedPointId = $"PT audit+é#{Guid.NewGuid():N}";
        var routeValue = Uri.EscapeDataString(decodedPointId);

        await using var services = CreateServices();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RelationalDbContext>().Database.MigrateAsync();

        var auditWriter = AuditWriter(services);
        var controller = BuildController(scope, auditWriter, decodedPointId, out var publisher);
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ControlDeliveryStatus.Delivered);

        Assert.IsType<AcceptedResult>(await controller.Control(
            routeValue, new PointController.PointControlRequest { Value = 1.0 }, CancellationToken.None));

        var audit = await controller.ControlAudit(routeValue, limit: 10, CancellationToken.None);
        var entry = Assert.Single(Assert.IsType<PointControlAuditResponse[]>(audit.Value));

        Assert.Equal(decodedPointId, entry.PointId);
    }

    [Fact]
    public async Task GatewayOffline_LeavesAFailedAuditRow_NotAPermanentlyPendingOne()
    {
        var pointId = $"PT-audit-{Guid.NewGuid():N}";
        await using var services = CreateServices();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RelationalDbContext>().Database.MigrateAsync();

        var controller = BuildController(scope, AuditWriter(services), pointId, out var publisher, gatewayId: "gw-offline");
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ControlDeliveryStatus.GatewayOffline);

        var response = Assert.IsType<ObjectResult>(await controller.Control(
            pointId, new PointController.PointControlRequest { Value = 1.0 }, CancellationToken.None));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);

        var audit = await controller.ControlAudit(pointId, limit: 10, CancellationToken.None);
        var entry = Assert.Single(Assert.IsType<PointControlAuditResponse[]>(audit.Value));

        Assert.Equal("failed", entry.Status);
        Assert.NotNull(entry.CompletedAt);
    }

    private static PointController BuildController(
        AsyncServiceScope scope,
        IControlAuditWriter auditWriter,
        string pointId,
        out Mock<IPointControlCommandPublisher> publisher,
        string gatewayId = "gw-1")
    {
        var point = new Point { DtId = $"urn:{pointId}", Id = pointId, Name = pointId, Writable = true };
        var device = new Device { DtId = "urn:dev", Id = "dev", Name = "dev", GatewayId = gatewayId };

        var twinView = new Mock<IAuthorizedTwinView>();
        twinView.Setup(v => v.CanWritePointAsync(
                    It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        twinView.Setup(v => v.GetPointDetailAsync(
                    It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TwinGetResult<PointDetail>.Ok(
                    new PointDetail { Point = point, Device = device }));
        twinView.Setup(v => v.GetPointAsync(
                    It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TwinGetResult<Point>.Ok(point));

        var schemaResolver = new Mock<IControlSchemaResolver>();
        schemaResolver.Setup(r => r.ResolveAsync(It.IsAny<Point>(), It.IsAny<Device?>()))
                      .ReturnsAsync((ControlSchema?)null);

        publisher = new Mock<IPointControlCommandPublisher>();

        var controller = new PointController(
            twinView.Object,
            new ControlTypeResolver(new ConfigGatewayConnectionRegistry(
                new Dictionary<string, string> { [gatewayId] = "bacnet-sim" }, "hono",
                new Dictionary<string, IReadOnlyDictionary<string, string>>(),
                new Dictionary<string, IReadOnlyDictionary<string, string>>())),
            schemaResolver.Object,
            Mock.Of<IControlResultBus>(),
            publisher.Object,
            scope.ServiceProvider.GetRequiredService<IPointControlRepository>(),
            auditWriter);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthorizationContext"] =
            new AuthorizationContext { UserId = "admin1", Role = "admin", Permissions = [] };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private static IControlAuditWriter AuditWriter(IServiceProvider services)
        => new ControlAuditWriter(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ControlAuditWriter>.Instance);

    private ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RelationalDbContext>(options => options.UseNpgsql(postgres.ConnectionString));
        services.AddScoped<IPointControlRepository, EfPointControlRepository>();
        return services.BuildServiceProvider();
    }
}
