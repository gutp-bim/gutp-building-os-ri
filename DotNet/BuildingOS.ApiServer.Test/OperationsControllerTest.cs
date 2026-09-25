using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BuildingOS.ApiServer.Test;

public class OperationsControllerTest
{
    [Fact]
    public async Task Summary_QueriesBothScalarsAndMapsThem()
    {
        var prometheus = new Mock<IPrometheusQueryClient>();
        prometheus.SetupGet(p => p.IsConfigured).Returns(true);
        prometheus
            .Setup(p => p.QueryScalarAsync(SystemStatusService.MsgRate1mQuery, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1842);
        prometheus
            .Setup(p => p.QueryScalarAsync(OperationsController.MsgRate1hAvgQuery, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1790);
        var c = new OperationsController(prometheus.Object);

        var result = Assert.IsType<OkObjectResult>((await c.Summary(default)).Result);
        var body = Assert.IsType<OperationsSummaryResponse>(result.Value);

        Assert.Equal(1842, body.MsgRate1m);
        Assert.Equal(1790, body.MsgRate1hAvg);
        Assert.True(body.MetricsAvailable);
    }

    [Fact]
    public async Task Summary_DegradesToNull_WhenPrometheusUnconfigured()
    {
        var prometheus = new Mock<IPrometheusQueryClient>();
        prometheus.SetupGet(p => p.IsConfigured).Returns(false);
        prometheus
            .Setup(p => p.QueryScalarAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((double?)null);
        var c = new OperationsController(prometheus.Object);

        var result = Assert.IsType<OkObjectResult>((await c.Summary(default)).Result);
        var body = Assert.IsType<OperationsSummaryResponse>(result.Value);

        Assert.Null(body.MsgRate1m);
        Assert.Null(body.MsgRate1hAvg);
        Assert.False(body.MetricsAvailable);
    }
}
