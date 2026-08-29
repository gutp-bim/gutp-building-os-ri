using System.Net;
using System.Text.Json;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

public class OxiGraphTwinAdminServiceTest
{
    private const string Sbco = "https://www.sbco.or.jp/ont/";

    private static OxiGraphTwinAdminService Create(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new TwinAdminMockHandler(handler)) { BaseAddress = new Uri("http://oxi:7878") };
        var client = new OxiGraphClient(http, "http://oxi:7878");
        var materializer = new OxiGraphIngestMaterializer(client);
        return new OxiGraphTwinAdminService(client, materializer);
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

    // The orphan count and the orphan sample share one graph pattern; both mention ?reason.
    private static bool IsOrphanQuery(string sparql) => sparql.Contains("?reason");

    // #336: the control-schema-issue candidate query is the only one selecting both ?dataType and
    // ?enumLabels together.
    private static bool IsControlSchemaIssueQuery(string sparql) =>
        sparql.Contains("?dataType") && sparql.Contains("?enumLabels");

    // Runs a preview against a store that reports no triples/gateways/collisions/orphans, and hands
    // back the materialized staging graph URI plus the control-schema-issue candidate query, so a test
    // can assert on the graph pattern the mode produced (mirrors CaptureOrphanQueryAsync).
    private static async Task<(string Graph, string SchemaQuery)> CaptureControlSchemaIssueQueryAsync(TwinImportMode mode)
    {
        var graph = "";
        var schemaQuery = "";
        var service = Create(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                graph = Uri.UnescapeDataString(req.RequestUri!.Query.Split("graph=")[1]);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (req.RequestUri!.AbsolutePath.EndsWith("/update")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (IsControlSchemaIssueQuery(q)) schemaQuery = q;
            return Bindings();
        });

        await service.PreviewImportAsync("ttl", mode);
        return ($"{graph}:materialized", schemaQuery);
    }

    // Runs a preview against a store that reports nothing, and hands back the materialized staging
    // graph URI plus the orphan-enumeration query, so a test can assert on the graph pattern the
    // mode produced. Preview evaluates the same canonical graph that ApplyImport would write.
    private static async Task<(string Graph, string OrphanQuery)> CaptureOrphanQueryAsync(TwinImportMode mode)
    {
        var graph = "";
        var orphanQuery = "";
        var service = Create(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                graph = Uri.UnescapeDataString(req.RequestUri!.Query.Split("graph=")[1]);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (req.RequestUri!.AbsolutePath.EndsWith("/update")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (q.Contains("SELECT DISTINCT ?pt ?reason")) orphanQuery = q;
            return Bindings();
        });

        await service.PreviewImportAsync("ttl", mode);
        return ($"{graph}:materialized", orphanQuery);
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
            // /query — distinguish the queries by content.
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (q.Contains("COUNT(*)")) return Bindings(new Dictionary<string, string> { ["n"] = "42" });
            if (q.Contains("DISTINCT ?gw") && q.Contains("COUNT(DISTINCT ?gw)")) return Bindings(new Dictionary<string, string> { ["n"] = "3" });
            // collision + orphan queries → none
            return Bindings();
        });

        var preview = await service.PreviewImportAsync("<a> <b> <c> .", TwinImportMode.Append);

        Assert.True(putGraph);
        Assert.True(droppedGraph);
        Assert.Equal(42, preview.TripleCount);
        Assert.Equal(3, preview.GatewayCount);
        Assert.True(preview.Valid);
        Assert.Empty(preview.Collisions);
        Assert.Equal(0, preview.OrphanCount);
        Assert.Empty(preview.Orphans);
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
            if (IsOrphanQuery(q)) return Bindings(); // hierarchy complete
            return Bindings(new Dictionary<string, string> { ["gw"] = "GW001", ["n"] = "2" }); // collision
        });

        var preview = await service.PreviewImportAsync("ttl", TwinImportMode.Append);

        Assert.False(preview.Valid);
        var c = Assert.Single(preview.Collisions);
        Assert.Equal("GW001", c.GatewayId);
        Assert.Equal(2, c.BuildingCount);
        Assert.Equal(0, preview.OrphanCount);
    }

    [Fact]
    public async Task PreviewImport_ReportsOrphans_WithReasonPerMissingLink()
    {
        // #291: the three UNION branches classify a point by the outermost missing link — no device,
        // a device with no spatial anchor at all, or an anchor that reaches no Building.
        var service = Create(req =>
        {
            if (req.Method == HttpMethod.Put) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (req.RequestUri!.AbsolutePath.EndsWith("/update")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (q.Contains("COUNT(*)")) return Bindings(new Dictionary<string, string> { ["n"] = "10" });
            if (q.Contains("COUNT(DISTINCT ?gw)")) return Bindings(new Dictionary<string, string> { ["n"] = "1" });
            if (q.Contains("COUNT(DISTINCT ?pt)")) return Bindings(new Dictionary<string, string> { ["n"] = "3" });
            if (q.Contains("SELECT DISTINCT ?pt ?reason"))
            {
                return Bindings(
                    new Dictionary<string, string> { ["pt"] = "urn:pt:1", ["reason"] = TwinOrphanReasons.NoDevice },
                    new Dictionary<string, string> { ["pt"] = "urn:pt:2", ["reason"] = TwinOrphanReasons.NoRoom },
                    new Dictionary<string, string> { ["pt"] = "urn:pt:3", ["reason"] = TwinOrphanReasons.NoBuildingPath });
            }
            return Bindings(); // no collisions
        });

        var preview = await service.PreviewImportAsync("ttl", TwinImportMode.Append);

        Assert.False(preview.Valid);
        Assert.Empty(preview.Collisions);
        Assert.Equal(3, preview.OrphanCount);
        Assert.Equal(
            new[] { TwinOrphanReasons.NoDevice, TwinOrphanReasons.NoRoom, TwinOrphanReasons.NoBuildingPath },
            preview.Orphans.Select(o => o.Reason));
        Assert.Equal(new[] { "urn:pt:1", "urn:pt:2", "urn:pt:3" }, preview.Orphans.Select(o => o.ResourceId));
    }

    [Fact]
    public async Task PreviewImport_Append_ResolvesReachabilityAcrossStagingAndDefaultGraph()
    {
        // #291 regression: an append merges the staged Turtle into the default graph, so adding an
        // EquipmentExt + PointExt under an existing Room/Level/Building orphans nothing. The chain
        // has to be matched per triple in the staging graph OR the default graph — one GRAPH around
        // a whole chain cuts the cross-graph link and reports the entire import as orphaned.
        var (graph, orphanQuery) = await CaptureOrphanQueryAsync(TwinImportMode.Append);

        Assert.Contains($"UNION {{ ?anyDev <{Sbco}hasPoint> ?pt . }}", orphanQuery);
        Assert.Contains($"UNION {{ ?anyRoom a <{Sbco}Room> . }}", orphanQuery);
        Assert.Contains($"UNION {{ ?anyBuilding <{Sbco}hasPart> ?anyFloor . }}", orphanQuery);
        // …while the candidates stay the staged points, so existing points are never re-judged.
        Assert.Contains($"GRAPH <{graph}> {{ ?pt a <{Sbco}PointExt> . }}", orphanQuery);
    }

    [Fact]
    public async Task PreviewImport_Replace_ResolvesReachabilityInTheStagingGraphOnly()
    {
        // A replace drops the default graph first, so nothing outside the staged Turtle may count as
        // a hierarchy link.
        var (graph, orphanQuery) = await CaptureOrphanQueryAsync(TwinImportMode.Replace);

        Assert.Contains($"GRAPH <{graph}> {{ ?anyDev <{Sbco}hasPoint> ?pt . }}", orphanQuery);
        Assert.DoesNotContain($"UNION {{ ?anyDev <{Sbco}hasPoint> ?pt . }}", orphanQuery);
        Assert.DoesNotContain($"UNION {{ ?anyRoom a <{Sbco}Room> . }}", orphanQuery);
    }

    [Fact]
    public async Task PreviewImport_AcceptsTheFloorLiteralAsAPathToTheBuilding()
    {
        // #291 regression: sbco:Room / sbco:locatedIn are optional in SBCO TTL, so a device joined to
        // its Level by the sbco:floor literal (the join the read side uses) is connected. The pattern
        // must offer that alternative next to the spatial chain, and must not require a Room anchor.
        var (graph, orphanQuery) = await CaptureOrphanQueryAsync(TwinImportMode.Replace);

        Assert.Contains($"GRAPH <{graph}> {{ ?anyDev <{Sbco}floor> ?anyFloorName . }}", orphanQuery);
        Assert.Contains($"GRAPH <{graph}> {{ ?anyFloor <{Sbco}name> ?anyFloorName . }}", orphanQuery);
    }

    [Fact]
    public async Task PreviewImport_AcceptsDirectLevelLocationAsAPathToTheBuilding()
    {
        var (graph, orphanQuery) = await CaptureOrphanQueryAsync(TwinImportMode.Replace);

        Assert.Contains($"GRAPH <{graph}> {{ ?anyDev <{Sbco}locatedIn> ?anyFloor . }}", orphanQuery);
        Assert.Contains($"GRAPH <{graph}> {{ ?anyFloor a <{Sbco}Level> . }}", orphanQuery);
        Assert.Contains($"GRAPH <{graph}> {{ ?anyBuilding <{Sbco}hasPart> ?anyFloor . }}", orphanQuery);
    }

    [Fact]
    public async Task PreviewImport_OrphanEnumerationIsCapped_ButCountIsExact()
    {
        // The count query is unbounded while the sample is LIMIT-ed, so a bulk import that orphans
        // more points than the cap still reports the true total to the operator.
        var sample = Enumerable.Range(0, 1000)
            .Select(i => new Dictionary<string, string> { ["pt"] = $"urn:pt:{i}", ["reason"] = TwinOrphanReasons.NoDevice })
            .ToArray();
        var limited = false;
        var service = Create(req =>
        {
            if (req.Method == HttpMethod.Put) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (req.RequestUri!.AbsolutePath.EndsWith("/update")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (q.Contains("COUNT(*)")) return Bindings(new Dictionary<string, string> { ["n"] = "10" });
            if (q.Contains("COUNT(DISTINCT ?gw)")) return Bindings(new Dictionary<string, string> { ["n"] = "1" });
            if (q.Contains("COUNT(DISTINCT ?pt)")) return Bindings(new Dictionary<string, string> { ["n"] = "5000" });
            if (q.Contains("SELECT DISTINCT ?pt ?reason"))
            {
                limited = q.Contains("LIMIT 1000");
                return Bindings(sample);
            }
            return Bindings();
        });

        var preview = await service.PreviewImportAsync("ttl", TwinImportMode.Append);

        Assert.True(limited);
        Assert.Equal(5000, preview.OrphanCount);
        Assert.Equal(1000, preview.Orphans.Count);
        Assert.False(preview.Valid);
    }

    [Fact]
    public async Task PreviewImport_ReportsControlSchemaIssue_WritablePointMissingDataType()
    {
        var service = Create(req =>
        {
            if (req.Method == HttpMethod.Put) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (req.RequestUri!.AbsolutePath.EndsWith("/update")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (q.Contains("COUNT(*)")) return Bindings(new Dictionary<string, string> { ["n"] = "10" });
            if (q.Contains("COUNT(DISTINCT ?gw)")) return Bindings(new Dictionary<string, string> { ["n"] = "1" });
            if (q.Contains("COUNT(DISTINCT ?pt)")) return Bindings(new Dictionary<string, string> { ["n"] = "0" });
            if (IsOrphanQuery(q)) return Bindings();
            if (IsControlSchemaIssueQuery(q))
            {
                // No "dataType" key at all — a writable point that never got a bos:dataType triple.
                return Bindings(new Dictionary<string, string> { ["pt"] = "urn:pt:1" });
            }
            return Bindings(); // no collisions
        });

        var preview = await service.PreviewImportAsync("ttl", TwinImportMode.Append);

        // Observation-only (#336): never affects Valid or blocks apply.
        Assert.True(preview.Valid);
        Assert.Equal(1, preview.ControlSchemaIssueCount);
        var issue = Assert.Single(preview.ControlSchemaIssues);
        Assert.Equal("urn:pt:1", issue.PointId);
        Assert.Equal(ControlSchemaIssueReasons.MissingDataType, issue.Reason);
    }

    [Fact]
    public async Task PreviewImport_ReportsControlSchemaIssue_EnumWithMalformedEnumLabels()
    {
        var service = Create(req =>
        {
            if (req.Method == HttpMethod.Put) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (req.RequestUri!.AbsolutePath.EndsWith("/update")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (q.Contains("COUNT(*)")) return Bindings(new Dictionary<string, string> { ["n"] = "10" });
            if (q.Contains("COUNT(DISTINCT ?gw)")) return Bindings(new Dictionary<string, string> { ["n"] = "1" });
            if (q.Contains("COUNT(DISTINCT ?pt)")) return Bindings(new Dictionary<string, string> { ["n"] = "0" });
            if (IsOrphanQuery(q)) return Bindings();
            if (IsControlSchemaIssueQuery(q))
            {
                return Bindings(
                    new Dictionary<string, string> { ["pt"] = "urn:pt:enum-bad-json", ["dataType"] = "enum", ["enumLabels"] = "not json" },
                    new Dictionary<string, string> { ["pt"] = "urn:pt:enum-missing", ["dataType"] = "enum" },
                    new Dictionary<string, string> { ["pt"] = "urn:pt:enum-ok", ["dataType"] = "enum", ["enumLabels"] = "{\"0\":\"OFF\",\"1\":\"ON\"}" },
                    new Dictionary<string, string> { ["pt"] = "urn:pt:boolean-ok", ["dataType"] = "boolean" });
            }
            return Bindings();
        });

        var preview = await service.PreviewImportAsync("ttl", TwinImportMode.Append);

        Assert.True(preview.Valid);
        Assert.Equal(2, preview.ControlSchemaIssueCount);
        Assert.Equal(
            new[] { "urn:pt:enum-bad-json", "urn:pt:enum-missing" },
            preview.ControlSchemaIssues.Select(i => i.PointId));
        Assert.All(preview.ControlSchemaIssues, i => Assert.Equal(ControlSchemaIssueReasons.MalformedEnumLabels, i.Reason));
    }

    [Fact]
    public async Task PreviewImport_ControlSchemaIssue_IgnoresReadOnlyPoints()
    {
        // Fail-open contract (#336): a read-only point with no bos:dataType is normal, not an issue —
        // ControlSchemaIssuePattern only candidates sbco:writable "true" points.
        var service = Create(req =>
        {
            if (req.Method == HttpMethod.Put) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (req.RequestUri!.AbsolutePath.EndsWith("/update")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (q.Contains("COUNT(*)")) return Bindings(new Dictionary<string, string> { ["n"] = "10" });
            if (q.Contains("COUNT(DISTINCT ?gw)")) return Bindings(new Dictionary<string, string> { ["n"] = "1" });
            if (q.Contains("COUNT(DISTINCT ?pt)")) return Bindings(new Dictionary<string, string> { ["n"] = "0" });
            if (IsOrphanQuery(q)) return Bindings();
            if (IsControlSchemaIssueQuery(q))
            {
                Assert.Contains($"<{Sbco}writable> \"true\"", q);
                return Bindings(); // read-only points never match the writable candidate
            }
            return Bindings();
        });

        var preview = await service.PreviewImportAsync("ttl", TwinImportMode.Append);

        Assert.Equal(0, preview.ControlSchemaIssueCount);
        Assert.Empty(preview.ControlSchemaIssues);
    }

    [Fact]
    public async Task PreviewImport_Append_ControlSchemaIssue_ScopesAcrossStagingAndDefaultGraph()
    {
        var (graph, schemaQuery) = await CaptureControlSchemaIssueQueryAsync(TwinImportMode.Append);

        Assert.Contains(
            $"{{ {{ GRAPH <{graph}> {{ ?pt a <{Sbco}PointExt> ; <{Sbco}writable> \"true\" . }} }} UNION {{ ?pt a <{Sbco}PointExt> ; <{Sbco}writable> \"true\" . }} }}",
            schemaQuery);
    }

    [Fact]
    public async Task PreviewImport_Replace_ControlSchemaIssue_ScopesToStagingGraphOnly()
    {
        var (graph, schemaQuery) = await CaptureControlSchemaIssueQueryAsync(TwinImportMode.Replace);

        Assert.Contains($"GRAPH <{graph}> {{ ?pt a <{Sbco}PointExt> ; <{Sbco}writable> \"true\" . }}", schemaQuery);
        Assert.DoesNotContain("UNION", schemaQuery);
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
