using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Infrastructure.Configuration;
using BuildingOS.Shared.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BuildingOS.ApiServer.Test;

public class SystemControllerTest
{
    private static AuthorizationContext Auth(string role) =>
        new() { UserId = "actor", Role = role, Permissions = [] };

    private static SystemController Build(
        AuthorizationContext auth,
        IIngressRejectionStatsService? ingressStats = null,
        ISystemStatusService? statusService = null,
        ISystemSettingsService? settings = null)
    {
        return new SystemController(
            statusService ?? Mock.Of<ISystemStatusService>(),
            Mock.Of<IEffectiveConfigService>(),
            ingressStats ?? Mock.Of<IIngressRejectionStatsService>(),
            settings ?? Mock.Of<ISystemSettingsService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Items = { ["AuthorizationContext"] = auth } },
            },
        };
    }

    [Fact]
    public async Task GetIngressRejections_NonAdmin_IsForbidden()
    {
        var c = Build(Auth("operator"));
        Assert.IsType<ForbidResult>(await c.GetIngressRejections(default));
    }

    [Fact]
    public async Task GetIngressRejections_Admin_ReturnsStatsFromService()
    {
        var stats = new IngressRejectionStats(
            [new IngressRejectionCount("no_building_path", 30)], MetricsAvailable: true);
        var ingressStats = new Mock<IIngressRejectionStatsService>();
        ingressStats.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stats);
        var c = Build(Auth("admin"), ingressStats.Object);

        var result = Assert.IsType<OkObjectResult>(await c.GetIngressRejections(default));

        Assert.Same(stats, result.Value);
    }

    private static readonly SystemStatus SampleStatus = new(
        [new ServiceStatus("building-os-api", "up")],
        new SystemKpis(MsgRate1m: 10, ControlReq5m: 1),
        MetricsAvailable: true);

    [Fact]
    public async Task GetStatus_NonAdmin_IsForbidden()
    {
        var c = Build(Auth("operator"));
        Assert.IsType<ForbidResult>(await c.GetStatus(default));
    }

    [Fact]
    public async Task GetStatus_Admin_AttachesEffectivePipelineThresholds()
    {
        var statusService = new Mock<ISystemStatusService>();
        var thresholds = PipelineKpiThresholds.Defaults with { EventLagP95WarnSeconds = 90 };
        // The status service needs the thresholds too (the Parquet stall window derives from them).
        statusService.Setup(s => s.GetStatusAsync(thresholds, It.IsAny<CancellationToken>())).ReturnsAsync(SampleStatus);
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetPipelineKpiThresholdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(thresholds);
        var c = Build(Auth("admin"), statusService: statusService.Object, settings: settings.Object);

        var result = Assert.IsType<OkObjectResult>(await c.GetStatus(default));
        var body = Assert.IsType<SystemStatus>(result.Value);

        Assert.Equal(thresholds, body.Thresholds);
        Assert.Equal(10, body.Kpis.MsgRate1m);
    }

    [Fact]
    public async Task GetStatus_SettingsStoreDown_FallsBackToDefaultThresholds()
    {
        // The status page must not fail because PostgreSQL (the settings store) is down — that is
        // exactly when an operator opens it.
        var statusService = new Mock<ISystemStatusService>();
        statusService.Setup(s => s.GetStatusAsync(PipelineKpiThresholds.Defaults, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleStatus);
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetPipelineKpiThresholdsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var c = Build(Auth("admin"), statusService: statusService.Object, settings: settings.Object);

        var result = Assert.IsType<OkObjectResult>(await c.GetStatus(default));
        var body = Assert.IsType<SystemStatus>(result.Value);

        Assert.Equal(PipelineKpiThresholds.Defaults, body.Thresholds);
    }

    // ── #527: reset the lake reader's learned point → building map ───────────────

    [Fact]
    public void ResetLakePointBuildings_Admin_ResetsAndReturns204()
    {
        var before = LakePointBuildingCacheProbe.Generation();

        var result = Build(Auth("admin")).ResetLakePointBuildings();

        Assert.IsType<NoContentResult>(result);
        Assert.NotEqual(before, LakePointBuildingCacheProbe.Generation());
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("viewer")]
    public void ResetLakePointBuildings_NonAdmin_IsForbidden(string role)
    {
        var before = LakePointBuildingCacheProbe.Generation();

        Assert.IsType<ForbidResult>(Build(Auth(role)).ResetLakePointBuildings());
        Assert.Equal(before, LakePointBuildingCacheProbe.Generation());
    }
}

/// <summary>Reads the reset generation without making it public API.</summary>
internal static class LakePointBuildingCacheProbe
{
    public static long Generation() => (long)typeof(BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake.LakePointBuildingCache)
        .GetProperty("Generation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(null)!;
}
