using System.Net;
using System.Text;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// Tests for the #224/push fan-out in OxiGraphSeedHostedService: after a seed import, every gateway
/// id present in the twin is signalled via <see cref="IPointListUpdatePublisher"/> — exactly once,
/// under its own gateway id (never mixed up with another gateway's), and never when a publisher isn't
/// wired (OSS/local without GatewayBridge).
/// </summary>
public class OxiGraphSeedHostedServicePointListPushTest
{
    // A nonexistent seed path takes the branch that runs uniqueness-check + point-list push without
    // actually needing to fake a Turtle import (TrySeedAsync logs + returns early on a missing file).
    private const string MissingSeedPath = "/tmp/nonexistent-oxigraph-seed-for-push-test.ttl";

    [Fact]
    public async Task RunAsync_MultipleGatewaysInTwin_EachPublishedExactlyOnceUnderItsOwnId()
    {
        var handler = new SeedQueryRoutingHandler(gatewayIds: ["GW001", "GW002", "GW003"]);
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var publisher = new RecordingPointListUpdatePublisher();
        var service = new OxiGraphSeedHostedService(client, NullLogger<OxiGraphSeedHostedService>.Instance, publisher);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.Equal(3, publisher.Calls.Count);
        var gatewayIds = publisher.Calls.Select(c => c.GatewayId).ToArray();
        Assert.Equal(new[] { "GW001", "GW002", "GW003" }, gatewayIds.OrderBy(x => x, StringComparer.Ordinal));
        // No gateway id was published more than once, and every call carried a distinct, non-empty id
        // (guards against a fan-out bug that republishes the last-seen id for every row).
        Assert.Equal(gatewayIds.Length, gatewayIds.Distinct().Count());
        Assert.All(publisher.Calls, c => Assert.Equal(string.Empty, c.Revision));
    }

    [Fact]
    public async Task RunAsync_NoGatewaysInTwin_PublishesNothing()
    {
        var handler = new SeedQueryRoutingHandler(gatewayIds: []);
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var publisher = new RecordingPointListUpdatePublisher();
        var service = new OxiGraphSeedHostedService(client, NullLogger<OxiGraphSeedHostedService>.Instance, publisher);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.Empty(publisher.Calls);
    }

    [Fact]
    public async Task RunAsync_NoPublisherWired_DoesNotThrow_AndSkipsPublish()
    {
        var handler = new SeedQueryRoutingHandler(gatewayIds: ["GW001"]);
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var service = new OxiGraphSeedHostedService(client, NullLogger<OxiGraphSeedHostedService>.Instance, pointListUpdatePublisher: null);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);
        // No exception + no publisher to assert against — reaching here is the assertion.
    }

    [Fact]
    public async Task RunAsync_PublishFails_LoggedAndNonFatal_DoesNotThrow()
    {
        var handler = new SeedQueryRoutingHandler(gatewayIds: ["GW001", "GW002"]);
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var publisher = new RecordingPointListUpdatePublisher { ThrowFor = "GW001" };
        var service = new OxiGraphSeedHostedService(client, NullLogger<OxiGraphSeedHostedService>.Instance, publisher);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        // #224/push is best-effort and per-gateway (never faults startup); GW001's failure does not
        // prevent GW002 from still being signalled.
        Assert.DoesNotContain(publisher.Calls, c => c.GatewayId == "GW001");
        Assert.Contains(publisher.Calls, c => c.GatewayId == "GW002");
    }

    private sealed class RecordingPointListUpdatePublisher : IPointListUpdatePublisher
    {
        public List<(string GatewayId, string Revision)> Calls { get; } = [];
        public string? ThrowFor { get; set; }

        public Task PublishAsync(string gatewayId, string revision, CancellationToken cancellationToken = default)
        {
            if (gatewayId == ThrowFor) throw new InvalidOperationException("simulated publish failure");
            Calls.Add((gatewayId, revision));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fakes the OxiGraph `/query` endpoint for the two SPARQL queries RunAsync issues once a (nonempty)
    /// seed path is set: the gateway-uniqueness check (always answered with no violations — out of scope
    /// for this test) and the distinct-gateway-id query (answered from the fixture list).
    /// </summary>
    private sealed class SeedQueryRoutingHandler(IReadOnlyList<string> gatewayIds) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var encodedBody = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
            var sparql = WebUtility.UrlDecode(encodedBody);

            var body = sparql.Contains("HAVING", StringComparison.Ordinal)
                ? @"{ ""results"": { ""bindings"": [] } }" // uniqueness check: no violations
                : $@"{{ ""results"": {{ ""bindings"": [{string.Join(",", gatewayIds.Select(g =>
                    $@"{{ ""gatewayId"": {{""type"":""literal"",""value"":""{g}""}} }}"))}] }} }}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/sparql-results+json"),
            };
        }
    }
}
