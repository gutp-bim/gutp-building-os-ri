using System.Net;
using System.Text;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// PublishAllAsync's per-gateway/per-query try/catch is deliberately broad (any publish or query
/// failure is logged and non-fatal, #414/#224) — but cancellation is not a "failure" and must
/// propagate rather than be logged as one, per review feedback on #414/#425.
/// </summary>
public class PointListUpdateBroadcasterTest
{
    private static OxiGraphClient MakeClient(IReadOnlyList<string> gatewayIds)
    {
        var handler = new QueryRoutingHandler(gatewayIds);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://oxi:7878") };
        return new OxiGraphClient(http, "http://oxi:7878");
    }

    [Fact]
    public async Task PublishAllAsync_PublisherCancelled_PropagatesInsteadOfLoggingAsFailure()
    {
        var client = MakeClient(["GW001", "GW002"]);
        var publisher = new CancellingPublisher(cancelFor: "GW001");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PointListUpdateBroadcaster.PublishAllAsync(
                client, publisher, NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task PublishAllAsync_CallerTokenAlreadyCancelled_PropagatesFromTheQueryStep()
    {
        var client = MakeClient(["GW001"]);
        var publisher = new CancellingPublisher(cancelFor: null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PointListUpdateBroadcaster.PublishAllAsync(client, publisher, NullLogger.Instance, cts.Token));
    }

    private sealed class CancellingPublisher(string? cancelFor) : IPointListUpdatePublisher
    {
        public Task PublishAsync(string gatewayId, string revision, CancellationToken cancellationToken = default)
        {
            if (gatewayId == cancelFor) throw new OperationCanceledException("simulated request abort");
            return Task.CompletedTask;
        }
    }

    private sealed class QueryRoutingHandler(IReadOnlyList<string> gatewayIds) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = $@"{{ ""results"": {{ ""bindings"": [{string.Join(",", gatewayIds.Select(g =>
                $@"{{ ""gatewayId"": {{""type"":""literal"",""value"":""{g}""}} }}"))}] }} }}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/sparql-results+json"),
            });
        }
    }
}
