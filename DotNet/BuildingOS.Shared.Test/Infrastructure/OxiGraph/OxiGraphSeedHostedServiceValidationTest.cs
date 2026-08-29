using System.Net;
using System.Text;
using System.Text.Json;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// Tests for the device template validation path in OxiGraphSeedHostedService.
/// Seed-import behaviour (TTL path) is covered by the existing smoke coverage;
/// these tests focus exclusively on the new validation step.
/// </summary>
public class OxiGraphSeedHostedServiceValidationTest : IDisposable
{
    // Each test writes a temp file and cleans up in Dispose.
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
    }

    // ── Cycle 6: validation triggers on startup ───────────────────────────

    [Fact]
    public async Task RunAsync_TemplatePathMissing_SkipsValidation()
    {
        // templatePath points to a non-existent file — service should log warning and NOT throw
        var svc = BuildService(
            sparqlJson: EmptySparql(),
            importStatus: HttpStatusCode.NoContent);

        // Should complete without exception even when file is absent
        await svc.RunAsync(seedTtlPath: null, templatePath: "/tmp/nonexistent_template_xyz.json", ct: default);
    }

    [Fact]
    public async Task RunAsync_ValidationPasses_DoesNotThrow()
    {
        // OxiGraph has DEV001/Sensor with Temperature and Humidity — matches template
        var sparql = BuildSparqlJson(
            Row("DEV001", "Sensor", "Temperature"),
            Row("DEV001", "Sensor", "Humidity"));

        var templatePath = WriteTempJson(
            new { @namespace = "ns", deviceType = "Sensor", className = "Sensor",
                  properties = new[] {
                      new { name = "Temperature", access = "read", pointType = "Temperature" },
                      new { name = "Humidity", access = "read", pointType = "Humidity" },
                  }});

        var svc = BuildService(sparqlJson: sparql, importStatus: HttpStatusCode.NoContent);
        // Must not throw
        await svc.RunAsync(seedTtlPath: null, templatePath: templatePath, ct: default);
    }

    [Fact]
    public async Task RunAsync_ValidationFails_ThrowsInvalidOperationException()
    {
        // OxiGraph has DEV001/Sensor with Temperature only — Humidity is missing
        var sparql = BuildSparqlJson(
            Row("DEV001", "Sensor", "Temperature"));

        var templatePath = WriteTempJson(
            new { @namespace = "ns", deviceType = "Sensor", className = "Sensor",
                  properties = new[] {
                      new { name = "Temperature", access = "read", pointType = "Temperature" },
                      new { name = "Humidity", access = "read", pointType = "Humidity" },
                  }});

        var svc = BuildService(sparqlJson: sparql, importStatus: HttpStatusCode.NoContent);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RunAsync(seedTtlPath: null, templatePath: templatePath, ct: default));

        Assert.Contains("DEV001", ex.Message);
        Assert.Contains("Humidity", ex.Message);
    }

    // ── gateway_id uniqueness (multi-gateway follow-up to #114) ────────────

    [Fact]
    public async Task RunAsync_GatewayIdSpansMultipleBuildings_ThrowsInvalidOperationException()
    {
        // GatewayUniquenessQuery flags gw-shared as spanning 2 buildings — every /query call in
        // this fake returns the same JSON, which is fine here: the readiness probe and the
        // (unused, publisher is null) point-list/control-schema queries don't care about content,
        // only ValidateGatewayUniquenessAsync's row shape matters for the throw.
        var sparql = "{\"results\":{\"bindings\":[" +
            "{\"gatewayId\":{\"type\":\"literal\",\"value\":\"gw-shared\"}," +
            "\"buildings\":{\"type\":\"literal\",\"value\":\"2\"}}]}}";

        var svc = BuildService(sparqlJson: sparql, importStatus: HttpStatusCode.NoContent);

        // A non-empty, non-existent seedTtlPath enters the validation branch (WaitForOxiGraph +
        // ValidateGatewayUniquenessAsync) while TrySeedAsync itself just warns-and-skips on the
        // missing file — the same pattern the existing templatePath tests use above.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RunAsync(
                seedTtlPath: "/tmp/oxigraph-seed-hosted-service-validation-test-does-not-exist.ttl",
                templatePath: null,
                ct: default));

        Assert.Contains("gw-shared", ex.Message);
        Assert.Contains("2 buildings", ex.Message);
    }

    [Fact]
    public async Task RunAsync_NoGatewaySpansMultipleBuildings_DoesNotThrow()
    {
        // HAVING (COUNT(DISTINCT ?building) > 1) means a clean twin returns zero rows — must not throw.
        var svc = BuildService(sparqlJson: EmptySparql(), importStatus: HttpStatusCode.NoContent);

        await svc.RunAsync(
            seedTtlPath: "/tmp/oxigraph-seed-hosted-service-validation-test-does-not-exist.ttl",
            templatePath: null,
            ct: default);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private OxiGraphSeedHostedService BuildService(string sparqlJson, HttpStatusCode importStatus)
    {
        var handler = new MultiResponseHandler(sparqlJson, importStatus);
        var http = new HttpClient(handler);
        var oxiClient = new OxiGraphClient(http, "http://oxigraph:7878");
        var materializer = new OxiGraphIngestMaterializer(oxiClient, NullLogger<OxiGraphIngestMaterializer>.Instance);
        return new OxiGraphSeedHostedService(oxiClient, materializer, NullLogger<OxiGraphSeedHostedService>.Instance);
    }

    private string WriteTempJson(object template)
    {
        var path = Path.GetTempFileName() + ".json";
        _tempFiles.Add(path);
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { template },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return path;
    }

    private static string EmptySparql() => BuildSparqlJson();

    private static string BuildSparqlJson(params (string equipmentId, string deviceType, string pointType)[] rows)
    {
        var bindings = rows.Select(r =>
            $"{{\"equipmentId\":{{\"type\":\"literal\",\"value\":\"{r.equipmentId}\"}}," +
            $"\"deviceType\":{{\"type\":\"literal\",\"value\":\"{r.deviceType}\"}}," +
            $"\"pointType\":{{\"type\":\"literal\",\"value\":\"{r.pointType}\"}}}}");
        return $"{{\"results\":{{\"bindings\":[{string.Join(",", bindings)}]}}}}";
    }

    private static (string, string, string) Row(string eid, string dt, string pt) => (eid, dt, pt);

    private sealed class MultiResponseHandler(string sparqlJson, HttpStatusCode importStatus)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            // /query → SPARQL JSON; /store → importStatus; /update → 204
            if (req.RequestUri!.AbsolutePath.EndsWith("/query"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sparqlJson, Encoding.UTF8, "application/sparql-results+json")
                });

            return Task.FromResult(new HttpResponseMessage(importStatus)
            {
                Content = new StringContent(string.Empty)
            });
        }
    }
}
