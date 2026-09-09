using BuildingOS.ConnectorWorker.Connectors;
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
    public async Task GetAsync_RoundsFractionalSamples_InsteadOfTruncating()
    {
        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Vectors =
            {
                [IngressRejectionStatsService.RejectionsByReasonQuery] =
                [
                    new PrometheusSample(new Dictionary<string, string> { ["result"] = "no_building_path" }, 30.9),
                ],
            },
        };
        var svc = new IngressRejectionStatsService(fake);

        var stats = await svc.GetAsync(CancellationToken.None);

        Assert.Equal(31, stats.Rejections[0].Count);
    }

    [Fact]
    public async Task GetAsync_CannotReportMqttOrAmqpIngressResults_AsGatewayRejections()
    {
        // #415 put a `result` tag on the MQTT/AMQP transport counters, which share the
        // building_os.ingress.messages instrument with the gRPC ingress. Two things keep the two
        // vocabularies apart and both are pinned here rather than left to prose:
        //  1. the PromQL is scoped to source="gateway-grpc", so bad_topic/bad_payload never reach it;
        //  2. an accepted transport message uses the same word ("published") the service filters out,
        //     so even a future widening of the selector cannot turn an accepted message into a rejection.
        Assert.Contains("source=\"gateway-grpc\"", IngressRejectionStatsService.RejectionsByReasonQuery);

        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Vectors =
            {
                [IngressRejectionStatsService.RejectionsByReasonQuery] =
                [
                    new PrometheusSample(
                        new Dictionary<string, string> { ["result"] = IngressTransportResults.Published }, 900),
                    new PrometheusSample(
                        new Dictionary<string, string> { ["result"] = "unknown_point" }, 7),
                ],
            },
        };
        var svc = new IngressRejectionStatsService(fake);

        var stats = await svc.GetAsync(CancellationToken.None);

        Assert.Equal("unknown_point", Assert.Single(stats.Rejections).Reason);
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
