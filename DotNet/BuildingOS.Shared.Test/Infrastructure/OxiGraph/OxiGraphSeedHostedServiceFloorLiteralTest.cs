using System.Net;
using System.Text;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// The startup seed never goes through the admin import's orphan preview, so equipment the builder
/// placed only by the <c>sbco:floor</c> literal (no <c>sbco:locatedIn</c>) — which the topology-only
/// read paths place in no Level or Building — would go unnoticed. The seed warns about it, and never
/// fails startup over it.
/// </summary>
public class OxiGraphSeedHostedServiceFloorLiteralTest
{
    private const string MissingSeedPath = "/tmp/nonexistent-oxigraph-seed-for-floor-literal-test.ttl";

    private static (OxiGraphSeedHostedService Service, RecordingLogger<OxiGraphSeedHostedService> Logger) Build(
        IReadOnlyList<string>? floorLiteralOnlyDevices)
    {
        var client = new OxiGraphClient(new HttpClient(new Handler(floorLiteralOnlyDevices)), "http://oxigraph:7878");
        var materializer = new OxiGraphIngestMaterializer(client, RecordingLogger<OxiGraphIngestMaterializer>.Null);
        var logger = new RecordingLogger<OxiGraphSeedHostedService>();
        return (new OxiGraphSeedHostedService(client, materializer, logger), logger);
    }

    [Fact]
    public async Task FloorLiteralOnlyEquipment_IsWarnedAbout_WithCountAndExamples()
    {
        var (service, logger) = Build(["AHU-1", "AHU-2"]);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("2 equipment")
            && e.Message.Contains("sbco:locatedIn")
            && e.Message.Contains("AHU-1"));
    }

    [Fact]
    public async Task NoFloorLiteralOnlyEquipment_LogsNothing()
    {
        var (service, logger) = Build([]);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("sbco:floor literal"));
    }

    [Fact]
    public async Task CheckFailure_IsLoggedAndNonFatal()
    {
        var (service, logger) = Build(null);

        await service.RunAsync(MissingSeedPath, templatePath: null, CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Floor-literal placement check after seed failed"));
    }

    private sealed class Handler(IReadOnlyList<string>? floorLiteralOnlyDevices) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
            var sparql = WebUtility.UrlDecode(body.StartsWith("query=", StringComparison.Ordinal) ? body[6..] : body);

            if (sparql == OxiGraphSeedHostedService.FloorLiteralOnlyQuery)
            {
                if (floorLiteralOnlyDevices is null) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                var bindings = string.Join(",", floorLiteralOnlyDevices.Select(d =>
                    $@"{{""devId"":{{""type"":""literal"",""value"":""{d}""}}}}"));
                return Ok($@"{{ ""results"": {{ ""bindings"": [{bindings}] }} }}");
            }
            // Every other seed-time query: an empty result.
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
