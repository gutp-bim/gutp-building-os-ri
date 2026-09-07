using BuildingOS.Shared.Infrastructure.Monitoring;

namespace BuildingOS.Shared.Test.Infrastructure.Monitoring;

public class IngressRejectionStatsServiceTest
{
    [Fact]
    public async Task GetAsync_GroupsRejectionsByReason_ExcludingAccepted()
    {
        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Vectors =
            {
                [IngressRejectionStatsService.RejectionsByReasonQuery] =
                [
                    new PrometheusSample(new Dictionary<string, string> { ["result"] = "published" }, 950),
                    new PrometheusSample(new Dictionary<string, string> { ["result"] = "no_building_path" }, 30),
                    new PrometheusSample(new Dictionary<string, string> { ["result"] = "unknown_point" }, 15),
                    new PrometheusSample(new Dictionary<string, string> { ["result"] = "gateway_mismatch" }, 5),
                ],
            },
        };
        var svc = new IngressRejectionStatsService(fake);

        var stats = await svc.GetAsync(CancellationToken.None);

        Assert.True(stats.MetricsAvailable);
        Assert.DoesNotContain(stats.Rejections, r => r.Reason == "published");
        Assert.Equal(3, stats.Rejections.Count);
        Assert.Equal("no_building_path", stats.Rejections[0].Reason);
        Assert.Equal(30, stats.Rejections[0].Count);
        Assert.Equal("unknown_point", stats.Rejections[1].Reason);
        Assert.Equal(15, stats.Rejections[1].Count);
        Assert.Equal("gateway_mismatch", stats.Rejections[2].Reason);
        Assert.Equal(5, stats.Rejections[2].Count);
    }

    [Fact]
    public async Task GetAsync_DegradesGracefully_WhenPrometheusUnconfigured()
    {
        var fake = new FakePrometheusClient { IsConfigured = false };
        var svc = new IngressRejectionStatsService(fake);

        var stats = await svc.GetAsync(CancellationToken.None);

        Assert.False(stats.MetricsAvailable);
        Assert.Empty(stats.Rejections);
    }
}
