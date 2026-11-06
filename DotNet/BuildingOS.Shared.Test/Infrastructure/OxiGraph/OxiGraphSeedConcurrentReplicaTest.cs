using System.Net;
using System.Text;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// #447 — what a second <c>WORKER_ROLE=all</c> replica does to the twin seed.
///
/// The seed prepares an isolated candidate, then replaces the live graph in one guarded UPDATE
/// transaction. Candidate preparation and cleanup may use other requests, but cannot expose a
/// half-cleared default graph. Concurrent edits after baseline capture refuse publication (#606).
/// Source graphs must be per-call so concurrent importers cannot overwrite each other's input;
/// the successful replace alone refreshes the retained provenance graph.
/// </summary>
public class OxiGraphSeedConcurrentReplicaTest
{
    private const string Turtle = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        <urn:bos:p1> a sbco:PointExt ; sbco:building "b1" ; sbco:gatewayId "gw1" .
        """;

    [Fact]
    public async Task Seed_SendsTheWholeDefaultGraphRebuildAsOneUpdateRequest()
    {
        var handler = new RecordingOxiGraphHandler();
        using var seed = new TempFile(Turtle);

        await BuildService(handler).RunAsync(seed.Path, templatePath: null, ct: default);

        // One request, not one per statement: a concurrent reader (or the other replica's own
        // uniqueness check) therefore sees the twin either wholly before or wholly after the rebuild.
        var update = Assert.Single(handler.Updates.Where(u => u.Contains("urn:bos:committed", StringComparison.Ordinal) && u.Contains("DELETE { ?s ?p ?o }", StringComparison.Ordinal)));
        Assert.Contains("FILTER NOT EXISTS", update, StringComparison.Ordinal);
        Assert.Contains("DELETE { ?s ?p ?o }", update, StringComparison.Ordinal);
        // …and it is the whole pass, not just the clear — the rebuild must not be able to land later.
        Assert.Contains("INSERT", update, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seed_UsesIsolatedSourceGraphs_AndRetainsProvenanceOnlyAtCommit()
    {
        // A concurrent importer must not overwrite another request's source before validation.
        // Temporary graphs are isolated; the retained audit graph is written at commit only.
        var handler = new RecordingOxiGraphHandler();
        using var seed = new TempFile(Turtle);

        await BuildService(handler).RunAsync(seed.Path, templatePath: null, ct: default);
        await BuildService(handler).RunAsync(seed.Path, templatePath: null, ct: default);

        // Repeated identical payloads use distinct temporary source graphs. Both successful commits
        // atomically refresh the same retained provenance graph.
        Assert.Equal(2, handler.Puts.Count);
        Assert.NotEqual(handler.Puts[0].Uri, handler.Puts[1].Uri);
        Assert.All(handler.Updates.Where(u => u.Contains("DELETE { ?s ?p ?o }", StringComparison.Ordinal)),
            u => Assert.Contains("GRAPH <urn:bos:twin-source>", u, StringComparison.Ordinal));
        Assert.Equal(handler.Puts[0].Body, handler.Puts[1].Body);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static OxiGraphSeedHostedService BuildService(HttpMessageHandler handler)
    {
        var client = new OxiGraphClient(new HttpClient(handler), "http://building-os.oxigraph:7878");
        var materializer = new OxiGraphIngestMaterializer(client, NullLogger<OxiGraphIngestMaterializer>.Instance);
        return new OxiGraphSeedHostedService(
            client, materializer, NullLogger<OxiGraphSeedHostedService>.Instance,
            pointListUpdatePublisher: null,
            startupTimeout: TimeSpan.FromSeconds(5));
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }

        public TempFile(string content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"seed_447_{Guid.NewGuid():N}.ttl");
            File.WriteAllText(Path, content);
        }

        public void Dispose()
        {
            try { File.Delete(Path); }
            catch (IOException) { /* best effort: a leftover temp file must not fail a test */ }
        }
    }

    /// <summary>Answers every SPARQL request with empty results and records what was sent where.</summary>
    private sealed class RecordingOxiGraphHandler : HttpMessageHandler
    {
        private const string EmptyResults = "{\"results\":{\"bindings\":[]}}";

        /// <summary>The <c>update=</c> bodies POSTed to /update, in order.</summary>
        public List<string> Updates { get; } = [];

        /// <summary>The staging PUTs, in order. The body is kept too, so a test can assert that a
        /// repeat import writes the same bytes and not merely to the same place.</summary>
        public List<(string Uri, string Body)> Puts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var uri = req.RequestUri!.ToString();

            if (req.Method == HttpMethod.Put)
            {
                var staged = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Puts.Add((uri, staged));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (uri.EndsWith("/update", StringComparison.Ordinal))
            {
                // application/x-www-form-urlencoded: '+' is a space, which UnescapeDataString leaves alone.
                var form = body.Replace("update=", "", StringComparison.Ordinal).Replace('+', ' ');
                Updates.Add(Uri.UnescapeDataString(form));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(WebUtility.UrlDecode(body).Contains("SELECT ?flag", StringComparison.Ordinal)
                    ? "{\"results\":{\"bindings\":[{\"flag\":{\"value\":\"true\"}}]}}" : EmptyResults,
                    Encoding.UTF8, "application/sparql-results+json"),
            };
        }
    }
}
