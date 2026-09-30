using System.Net;
using System.Text;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// #517 at startup: the seed skips the admin import's preview, so business ids shared by nodes of one
/// type — which would share every grant (#504) — are logged as an error. Never fatal: an existing twin
/// with duplicates must still start (the admin import is where they are refused).
/// </summary>
public class OxiGraphSeedHostedServiceIdCollisionTest
{
    private const string MissingSeedPath = "/tmp/nonexistent-oxigraph-seed-for-id-collision-test.ttl";

    private static (OxiGraphSeedHostedService Service, RecordingLogger<OxiGraphSeedHostedService> Logger) Build(
        IReadOnlyList<(string Cls, string Id, int N)>? collisions, bool timeOut = false)
    {
        var client = new OxiGraphClient(new HttpClient(new Handler(collisions, timeOut)), "http://oxigraph:7878");
        var materializer = new OxiGraphIngestMaterializer(client, RecordingLogger<OxiGraphIngestMaterializer>.Null);
        var logger = new RecordingLogger<OxiGraphSeedHostedService>();
        return (new OxiGraphSeedHostedService(client, materializer, logger), logger);
    }

    [Fact]
    public async Task DuplicateIds_AreLoggedAsAnError_WithTypeAndExamples()
    {
        var (service, logger) = Build([("https://www.sbco.or.jp/ont/Room", "501", 2)]);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("space 501 (2 nodes)") && e.Message.Contains("sbco:id"));
    }

    [Fact]
    public async Task NoDuplicates_LogsNothing()
    {
        var (service, logger) = Build([]);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("sbco:id"));
    }

    [Fact]
    public async Task CheckFailureOrTimeout_IsNonFatal()
    {
        var (service, logger) = Build(null, timeOut: true);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Business-id uniqueness check after seed failed"));
    }

    private sealed class Handler(IReadOnlyList<(string Cls, string Id, int N)>? collisions, bool timeOut) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
            var sparql = WebUtility.UrlDecode(body.StartsWith("query=", StringComparison.Ordinal) ? body[6..] : body);

            if (sparql == OxiGraphSeedHostedService.IdCollisionQuery)
            {
                if (timeOut) throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
                var bindings = string.Join(",", collisions!.Select(c =>
                    $@"{{""cls"":{{""type"":""uri"",""value"":""{c.Cls}""}},""id"":{{""type"":""literal"",""value"":""{c.Id}""}},""n"":{{""type"":""literal"",""value"":""{c.N}""}}}}"));
                return Ok($@"{{ ""results"": {{ ""bindings"": [{bindings}] }} }}");
            }
            return Ok(@"{ ""results"": { ""bindings"": [] } }");
        }

        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/sparql-results+json"),
        };
    }
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public static RecordingLogger<T> Null => new();

        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries { get { lock (_entries) return _entries.ToArray(); } }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add(new Entry(logLevel, formatter(state, exception), exception));
        }
    }
}
