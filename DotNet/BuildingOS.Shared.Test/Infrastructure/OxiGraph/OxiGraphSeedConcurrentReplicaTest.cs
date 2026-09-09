using System.Net;
using System.Text;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// #447 — what a second <c>WORKER_ROLE=all</c> replica does to the twin seed.
///
/// The seed replaces the default graph (<c>DROP DEFAULT</c> + rebuild), so the question is whether two
/// replicas doing it at once can leave a reader looking at a half-cleared twin. They cannot, and the
/// reason is a property of *this* code rather than of OxiGraph's concurrency: the whole rebuild is sent
/// as one SPARQL UPDATE request, which OxiGraph commits in a single transaction. Split it into a
/// request per statement — an entirely natural-looking refactor — and the guarantee is gone with no
/// test to notice, which is what these characterization tests exist to prevent. The behaviour they
/// pin predates #447 — that change added the tests, not the property — so they are a guard against
/// future refactors rather than proof of a fix.
///
/// The remaining cost of a duplicate seed (importing the same file twice, pushing the point-list
/// update twice) is convergent, not corrupting — see <see cref="OxiGraphSeedHostedService"/>.
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
        var update = Assert.Single(handler.Updates);
        Assert.Contains("DROP DEFAULT", update, StringComparison.Ordinal);
        // …and it is the whole pass, not just the clear — the rebuild must not be able to land later.
        Assert.Contains("INSERT", update, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seed_StagesTheSourceRdfUnderAFixedGraphUri_SoARepeatImportOverwritesIt()
    {
        // The staging PUT is the one write that precedes the atomic UPDATE. It targets a fixed graph
        // URI and is a full replace, so a second replica staging the same seed writes identical bytes
        // to the same place instead of accumulating a graph per import.
        var handler = new RecordingOxiGraphHandler();
        using var seed = new TempFile(Turtle);

        await BuildService(handler).RunAsync(seed.Path, templatePath: null, ct: default);
        await BuildService(handler).RunAsync(seed.Path, templatePath: null, ct: default);

        // Both halves of the claim: the same graph URI (so imports overwrite instead of accumulating)
        // AND the same bytes (so whichever replica lands second leaves the staged RDF unchanged).
        // Comparing only the URI would let a per-replica payload through.
        Assert.Equal(2, handler.Puts.Count);
        Assert.Equal(handler.Puts[0].Uri, handler.Puts[1].Uri);
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
                Content = new StringContent(EmptyResults, Encoding.UTF8, "application/sparql-results+json"),
            };
        }
    }
}
