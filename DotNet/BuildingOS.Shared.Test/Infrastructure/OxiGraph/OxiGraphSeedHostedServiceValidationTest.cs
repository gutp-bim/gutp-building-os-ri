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

    // ── helpers ───────────────────────────────────────────────────────────

    private OxiGraphSeedHostedService BuildService(string sparqlJson, HttpStatusCode importStatus)
    {
        var handler = new MultiResponseHandler(sparqlJson, importStatus);
        var http = new HttpClient(handler);
        var oxiClient = new OxiGraphClient(http, "http://oxigraph:7878");
        return new OxiGraphSeedHostedService(oxiClient, NullLogger<OxiGraphSeedHostedService>.Instance);
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
