using System.Net;
using System.Text;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// #414: ApplyImportAsync must signal every gateway named in the twin after a successful import,
/// the same best-effort push OxiGraphSeedHostedService does after a startup seed (#224) — the
/// admin apply path previously never called the publisher at all.
/// </summary>
public class OxiGraphTwinAdminServicePointListPushTest
{
    private static OxiGraphTwinAdminService Create(
        IReadOnlyList<string> gatewayIds, IPointListUpdatePublisher? publisher)
    {
        var handler = new ApplyQueryRoutingHandler(gatewayIds);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://oxi:7878") };
        var client = new OxiGraphClient(http, "http://oxi:7878");
        var materializer = new OxiGraphIngestMaterializer(client);
        return new OxiGraphTwinAdminService(client, materializer, logger: null, publisher);
    }

    [Fact]
    public async Task ApplyImportAsync_Replace_PublishesToEveryGatewayInTwin()
    {
        var publisher = new RecordingPointListUpdatePublisher();
        var service = Create(["GW001", "GW002"], publisher);

        await service.ApplyImportAsync("ttl", TwinImportMode.Replace);

        Assert.Equal(2, publisher.Calls.Count);
        Assert.Equal(
            new[] { "GW001", "GW002" },
            publisher.Calls.Select(c => c.GatewayId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(publisher.Calls, c => Assert.Equal(string.Empty, c.Revision));
    }

    [Fact]
    public async Task ApplyImportAsync_Append_PublishesToEveryGatewayInTwin()
    {
        var publisher = new RecordingPointListUpdatePublisher();
        var service = Create(["GW001"], publisher);

        await service.ApplyImportAsync("ttl", TwinImportMode.Append);

        Assert.Single(publisher.Calls);
        Assert.Equal("GW001", publisher.Calls[0].GatewayId);
    }

    [Fact]
    public async Task ApplyImportAsync_NoPublisherWired_DoesNotThrow()
    {
        var service = Create(["GW001"], publisher: null);

        await service.ApplyImportAsync("ttl", TwinImportMode.Replace);
        // No exception + no publisher to assert against — reaching here is the assertion.
    }

    [Fact]
    public async Task ApplyImportAsync_PublishFails_LoggedAndNonFatal_DoesNotThrow()
    {
        var publisher = new RecordingPointListUpdatePublisher { ThrowFor = "GW001" };
        var service = Create(["GW001", "GW002"], publisher);

        await service.ApplyImportAsync("ttl", TwinImportMode.Replace);

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
    /// Fakes the OxiGraph HTTP surface ApplyImportAsync drives: graph load (PUT), the materialization
    /// update (POST /update), and the distinct-gateway-id query (POST /query, answered from the
    /// fixture list) that PointListUpdateBroadcaster issues afterwards.
    /// </summary>
    private sealed class ApplyQueryRoutingHandler(IReadOnlyList<string> gatewayIds) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Put || request.RequestUri!.AbsolutePath.EndsWith("/update"))
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            var encodedBody = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
            const string queryPrefix = "query=";
            var encodedValue = encodedBody.StartsWith(queryPrefix, StringComparison.Ordinal)
                ? encodedBody[queryPrefix.Length..]
                : encodedBody;
            var sparql = WebUtility.UrlDecode(encodedValue);

            if (sparql == PointListUpdateBroadcaster.DistinctGatewayQuery)
            {
                var body = $@"{{ ""results"": {{ ""bindings"": [{string.Join(",", gatewayIds.Select(g =>
                    $@"{{ ""gatewayId"": {{""type"":""literal"",""value"":""{g}""}} }}"))}] }} }}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/sparql-results+json"),
                };
            }

            throw new InvalidOperationException($"unexpected SPARQL query in test: {sparql}");
        }
    }
}
