using BuildingOS.ConnectorWorker.Infrastructure.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using NATS.Client.Core;

namespace BuildingOS.Shared.Test.Infrastructure.Health;

public class NatsReadinessHealthCheckTest
{
    private static async Task<HealthCheckResult> Check(NatsConnectionState state)
    {
        var conn = new Mock<INatsConnection>();
        conn.SetupGet(c => c.ConnectionState).Returns(state);
        var sut = new NatsReadinessHealthCheck(conn.Object);
        return await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
    }

    [Fact]
    public async Task Ready_WhenNatsConnectionOpen()
    {
        var result = await Check(NatsConnectionState.Open);
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData(NatsConnectionState.Connecting)]
    [InlineData(NatsConnectionState.Reconnecting)]
    [InlineData(NatsConnectionState.Closed)]
    public async Task NotReady_WhenNatsConnectionNotOpen(NatsConnectionState state)
    {
        var result = await Check(state);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(state.ToString(), result.Description);
    }
}
