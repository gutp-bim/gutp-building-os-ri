using System.Net;
using System.Text.Json;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

public class OxiGraphTwinAdminServiceTest
{
    private static OxiGraphTwinAdminService Create(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new TwinAdminMockHandler(handler)) { BaseAddress = new Uri("http://oxi:7878") };
        return new OxiGraphTwinAdminService(new OxiGraphClient(http, "http://oxi:7878"));
    }

    private static HttpResponseMessage Bindings(params Dictionary<string, string>[] rows)
    {
        var payload = new
        {
            results = new
            {
                bindings = rows.Select(r => r.ToDictionary(kv => kv.Key, kv => new { value = kv.Value })),
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    [Fact]
    public async Task PreviewImport_LoadsStagingGraph_ReturnsCounts_AndDrops()
    {
        var putGraph = false;
        var droppedGraph = false;
        var service = Create(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri!.Query.Contains("graph="))
            {
                putGraph = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (req.RequestUri!.AbsolutePath.EndsWith("/update"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("DROP") && Uri.UnescapeDataString(body).Contains("GRAPH")) droppedGraph = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            // /query — distinguish the three queries by content.
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (q.Contains("COUNT(*)")) return Bindings(new Dictionary<string, string> { ["n"] = "42" });
            if (q.Contains("DISTINCT ?gw") && q.Contains("COUNT(DISTINCT ?gw)")) return Bindings(new Dictionary<string, string> { ["n"] = "3" });
            // collision query → none
            return Bindings();
        });

        var preview = await service.PreviewImportAsync("<a> <b> <c> .");

        Assert.True(putGraph);
        Assert.True(droppedGraph);
        Assert.Equal(42, preview.TripleCount);
        Assert.Equal(3, preview.GatewayCount);
        Assert.True(preview.Valid);
        Assert.Empty(preview.Collisions);
    }

    [Fact]
    public async Task PreviewImport_ReportsGatewayCollisions()
    {
        var service = Create(req =>
        {
            if (req.Method == HttpMethod.Put) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (req.RequestUri!.AbsolutePath.EndsWith("/update")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (q.Contains("COUNT(*)")) return Bindings(new Dictionary<string, string> { ["n"] = "10" });
            if (q.Contains("COUNT(DISTINCT ?gw)")) return Bindings(new Dictionary<string, string> { ["n"] = "2" });
            return Bindings(new Dictionary<string, string> { ["gw"] = "GW001", ["n"] = "2" }); // collision
        });

        var preview = await service.PreviewImportAsync("ttl");

        Assert.False(preview.Valid);
        var c = Assert.Single(preview.Collisions);
        Assert.Equal("GW001", c.GatewayId);
        Assert.Equal(2, c.BuildingCount);
    }

    [Fact]
    public async Task RunReadOnlyQuery_CapsRows_AndFlagsTruncated()
    {
        var rows = Enumerable.Range(0, 5).Select(i => new Dictionary<string, string> { ["s"] = $"s{i}" }).ToArray();
        var service = Create(req => Bindings(rows));

        var result = await service.RunReadOnlyQueryAsync("SELECT ?s WHERE { ?s ?p ?o }", maxRows: 2, TimeSpan.FromSeconds(5));

        Assert.Equal(2, result.RowCount);
        Assert.True(result.Truncated);
        Assert.Equal(new[] { "s" }, result.Columns);
    }

    [Fact]
    public async Task RunReadOnlyQuery_RejectsNonReadOnly()
    {
        var service = Create(_ => Bindings());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RunReadOnlyQueryAsync("DROP ALL", 10, TimeSpan.FromSeconds(5)));
    }
}

internal sealed class TwinAdminMockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(handler(request));
}
