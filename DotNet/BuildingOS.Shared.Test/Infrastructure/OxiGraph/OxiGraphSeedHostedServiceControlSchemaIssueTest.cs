using System.Net;
using System.Text;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// Tests for the #336 post-seed control-schema-issue check in OxiGraphSeedHostedService: the startup
/// seed path (OXIGRAPH_SEED_TTL_PATH) never goes through
/// OxiGraphTwinAdminService.PreviewImportAsync/ApplyImportAsync, so it is the one place the #331/#332
/// class of incident (a broken twin fixture with no usable bos: control schema) would otherwise go
/// undetected until a control request silently fails open. The check must never fail startup —
/// fail-open at control time means fail-open here too.
/// </summary>
public class OxiGraphSeedHostedServiceControlSchemaIssueTest
{
    private const string MissingSeedPath = "/tmp/nonexistent-oxigraph-seed-for-schema-issue-test.ttl";

    [Fact]
    public async Task RunAsync_ControlSchemaIssuesFound_LogsWarning_AndDoesNotThrow()
    {
        var handler = new SeedQueryRoutingHandler(controlSchemaIssueRows:
        [
            new Dictionary<string, string> { ["pt"] = "urn:pt:writable-1" },
        ]);
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var materializer = new OxiGraphIngestMaterializer(client, RecordingLogger<OxiGraphIngestMaterializer>.Null);
        var logger = new RecordingLogger<OxiGraphSeedHostedService>();
        var service = new OxiGraphSeedHostedService(client, materializer, logger);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Control-schema issues detected after seed")
            && e.Message.Contains("missing_datatype"));
    }

    [Fact]
    public async Task RunAsync_ControlSchemaIssuesFound_ReasonBreakdownSumsToTheReportedTotal()
    {
        // #336 review: the total logged is the exact (uncapped) count, so the per-reason breakdown
        // computed from the same classification must always sum to it — no separate cap between the
        // two that could make them silently disagree.
        var handler = new SeedQueryRoutingHandler(controlSchemaIssueRows:
        [
            new Dictionary<string, string> { ["pt"] = "urn:pt:1" },
            new Dictionary<string, string> { ["pt"] = "urn:pt:2" },
            new Dictionary<string, string> { ["pt"] = "urn:pt:3", ["dataType"] = "enum", ["enumLabels"] = "not json" },
        ]);
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var materializer = new OxiGraphIngestMaterializer(client, RecordingLogger<OxiGraphIngestMaterializer>.Null);
        var logger = new RecordingLogger<OxiGraphSeedHostedService>();
        var service = new OxiGraphSeedHostedService(client, materializer, logger);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("3 writable point(s)")
            && e.Message.Contains("missing_datatype=2")
            && e.Message.Contains("malformed_enum_labels=1"));
    }

    [Fact]
    public async Task RunAsync_NoControlSchemaIssues_LogsNothing()
    {
        var handler = new SeedQueryRoutingHandler(controlSchemaIssueRows: []);
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var materializer = new OxiGraphIngestMaterializer(client, RecordingLogger<OxiGraphIngestMaterializer>.Null);
        var logger = new RecordingLogger<OxiGraphSeedHostedService>();
        var service = new OxiGraphSeedHostedService(client, materializer, logger);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Control-schema issues detected"));
    }

    [Fact]
    public async Task RunAsync_ControlSchemaCheckQueryFails_LoggedAndNonFatal_DoesNotThrow()
    {
        var handler = new SeedQueryRoutingHandler(controlSchemaIssueRows: null); // null → simulate a failing query
        var client = new OxiGraphClient(new HttpClient(handler), "http://oxigraph:7878");
        var materializer = new OxiGraphIngestMaterializer(client, RecordingLogger<OxiGraphIngestMaterializer>.Null);
        var logger = new RecordingLogger<OxiGraphSeedHostedService>();
        var service = new OxiGraphSeedHostedService(client, materializer, logger);

        // Must not throw even though the control-schema-issue query itself errors (fail-open, #336).
        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Control-schema issue check after seed failed"));
    }

    /// <summary>
    /// Fakes the OxiGraph `/query` endpoint for every SPARQL query RunAsync issues once a (nonempty)
    /// seed path is set, routing by exact match against the service's own query constants (mirrors
    /// OxiGraphSeedHostedServicePointListPushTest's handler). <c>controlSchemaIssueRows</c> null
    /// simulates a transport failure for that one query; an empty/non-empty list controls its result.
    /// </summary>
    private sealed class SeedQueryRoutingHandler(IReadOnlyList<Dictionary<string, string>>? controlSchemaIssueRows)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var encodedBody = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
            const string queryPrefix = "query=";
            var encodedValue = encodedBody.StartsWith(queryPrefix, StringComparison.Ordinal)
                ? encodedBody[queryPrefix.Length..]
                : encodedBody;
            var sparql = WebUtility.UrlDecode(encodedValue);

            if (sparql == OxiGraphSeedHostedService.ReadinessQuery)
                return Ok(@"{ ""results"": { ""bindings"": [] } }");
            if (sparql == OxiGraphSeedHostedService.GatewayUniquenessQuery)
                return Ok(@"{ ""results"": { ""bindings"": [] } }");
            if (sparql == OxiGraphSeedHostedService.DistinctGatewayQuery)
                return Ok(@"{ ""results"": { ""bindings"": [] } }");
            if (sparql == OxiGraphSeedHostedService.ControlSchemaIssueQuery)
            {
                if (controlSchemaIssueRows is null)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);

                var bindings = string.Join(",", controlSchemaIssueRows.Select(row =>
                    "{" + string.Join(",", row.Select(kv =>
                        $@"""{kv.Key}"":{{""type"":""literal"",""value"":""{kv.Value}""}}")) + "}"));
                return Ok($@"{{ ""results"": {{ ""bindings"": [{bindings}] }} }}");
            }

            throw new InvalidOperationException($"unexpected SPARQL query in test: {sparql}");
        }

        private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/sparql-results+json"),
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
