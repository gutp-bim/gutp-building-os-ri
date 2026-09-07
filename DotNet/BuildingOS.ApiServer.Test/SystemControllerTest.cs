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
        IIngressRejectionStatsService? ingressStats = null)
    {
        return new SystemController(
            Mock.Of<ISystemStatusService>(),
            Mock.Of<IEffectiveConfigService>(),
            ingressStats ?? Mock.Of<IIngressRejectionStatsService>())
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
}
