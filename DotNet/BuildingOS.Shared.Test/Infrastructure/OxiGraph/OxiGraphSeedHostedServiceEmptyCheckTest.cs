using System.Net;
using System.Text;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// #484: the class doc claims OXIGRAPH_SEED_TTL_PATH only imports "when the store is empty", but
/// TrySeedAsync used to run unconditionally — silently discarding any runtime twin state (e.g. via
/// the admin import API) on every restart. These tests pin the store-empty gate added to fix that:
/// the readiness probe's own row count decides whether the seed import runs at all.
/// </summary>
public class OxiGraphSeedHostedServiceEmptyCheckTest : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public async Task RunAsync_StoreEmpty_ImportsSeed()
    {
        var seedPath = WriteTempTurtle();
        var handler = new EmptyCheckRoutingHandler(storeIsEmpty: true);
        var svc = BuildService(handler);

        await svc.RunAsync(seedTtlPath: seedPath, templatePath: null, ct: default);

        Assert.True(handler.WriteCount > 0, "an empty store must be seeded");
    }

    [Fact]
    public async Task RunAsync_StoreNotEmpty_SkipsSeedImport()
    {
        var seedPath = WriteTempTurtle();
        var handler = new EmptyCheckRoutingHandler(storeIsEmpty: false);
        var svc = BuildService(handler);

        // Must not throw, and must not touch the write endpoints at all — a non-empty store means
        // either a prior seed already ran or the admin API has since changed the twin, and #484 is
        // exactly the silent-data-loss bug that reseeding here would reproduce.
        await svc.RunAsync(seedTtlPath: seedPath, templatePath: null, ct: default);

        Assert.Equal(0, handler.WriteCount);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private string WriteTempTurtle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oxigraph_seed_484_{Guid.NewGuid():N}.ttl");
        File.WriteAllText(path, "@prefix sbco: <https://www.sbco.or.jp/ont/> .\n");
        _tempFiles.Add(path);
        return path;
    }

    private static OxiGraphSeedHostedService BuildService(HttpMessageHandler handler)
    {
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var materializer = new OxiGraphIngestMaterializer(client, NullLogger<OxiGraphIngestMaterializer>.Instance);
        return new OxiGraphSeedHostedService(client, materializer, NullLogger<OxiGraphSeedHostedService>.Instance);
    }

    /// <summary>
    /// Routes /query by exact SPARQL text like <see cref="OxiGraphSeedHostedServicePointListPushTest"/>'s
    /// handler, but reports the readiness probe (#321) as empty or non-empty per <paramref
    /// name="storeIsEmpty"/> — the only query whose row count the #484 gate reads. Every non-/query
    /// request (the Turtle staging PUT and the materialization SPARQL UPDATE) is counted as a write,
    /// so the tests can assert TrySeedAsync did or did not actually run without depending on
    /// OxiGraphIngestMaterializer's internal call sequence.
    /// </summary>
    private sealed class EmptyCheckRoutingHandler(bool storeIsEmpty) : HttpMessageHandler
    {
        private int _writeCount;
        public int WriteCount => Volatile.Read(ref _writeCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/query")
            {
                Interlocked.Increment(ref _writeCount);
                return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent(string.Empty) };
            }

            var encodedBody = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
            const string queryPrefix = "query=";
            var encodedValue = encodedBody.StartsWith(queryPrefix, StringComparison.Ordinal)
                ? encodedBody[queryPrefix.Length..]
                : encodedBody;
            var sparql = WebUtility.UrlDecode(encodedValue);

            string body;
            if (sparql == OxiGraphSeedHostedService.ReadinessQuery)
                body = storeIsEmpty
                    ? @"{ ""results"": { ""bindings"": [] } }"
                    : @"{ ""results"": { ""bindings"": [{ ""s"": {""type"":""uri"",""value"":""urn:existing""} }] } }";
            else if (sparql == OxiGraphSeedHostedService.GatewayUniquenessQuery)
                body = @"{ ""results"": { ""bindings"": [] } }"; // no violations — out of scope here
            else if (sparql == OxiGraphSeedHostedService.ControlSchemaIssueQuery)
                body = @"{ ""results"": { ""bindings"": [] } }"; // no issues — out of scope here
            else if (sparql == OxiGraphSeedHostedService.DistinctGatewayQuery)
                body = @"{ ""results"": { ""bindings"": [] } }"; // no gateways to push — out of scope here
            else
                throw new InvalidOperationException($"unexpected SPARQL query in test: {sparql}");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/sparql-results+json"),
            };
        }
    }
}
