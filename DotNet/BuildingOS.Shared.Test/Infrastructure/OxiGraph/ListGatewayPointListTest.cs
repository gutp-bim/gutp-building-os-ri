using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// Tests for the gateway-scoped point list export (#224 / U1): twin → GatewayPointEntry mapping
/// including native addressing (localId / BACnet), control schema, and device grouping.
/// </summary>
public class ListGatewayPointListTest
{
    private static OxiGraphDigitalTwinDatabase BuildDb(string responseJson)
    {
        var handler = new PointListFakeHandler(responseJson);
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        return new OxiGraphDigitalTwinDatabase(client, cache);
    }

    /// <summary>
    /// Projects the former single-query fixture rows into the three result shapes used by the
    /// optimized point-list implementation. This keeps the mapping examples compact while also
    /// exercising the point, attribute, and device query boundaries independently.
    /// </summary>
    private sealed class PointListFakeHandler(string sourceJson) : HttpMessageHandler
    {
        private static readonly IReadOnlyDictionary<string, string> AttributeUris =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["localId"] = "https://www.sbco.or.jp/ont/localId",
                ["devIdBac"] = "https://www.sbco.or.jp/ont/deviceIdBacnet",
                ["objType"] = "https://www.sbco.or.jp/ont/objectTypeBacnet",
                ["instNo"] = "https://www.sbco.or.jp/ont/instanceNoBacnet",
                ["unit"] = "https://www.sbco.or.jp/ont/unit",
                ["writable"] = "https://www.sbco.or.jp/ont/writable",
                ["dataType"] = "http://buildingos.gutp.jp/ontology#dataType",
                ["minV"] = "http://buildingos.gutp.jp/ontology#minValue",
                ["maxV"] = "http://buildingos.gutp.jp/ontology#maxValue",
                ["enumLabels"] = "http://buildingos.gutp.jp/ontology#enumLabels",
            };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var encodedBody = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
            var sparql = WebUtility.UrlDecode(encodedBody);
            var sourceBindings = JsonNode.Parse(sourceJson)!["results"]!["bindings"]!.AsArray();
            var projected = new JsonArray();

            foreach (var node in sourceBindings)
            {
                var row = node!.AsObject();
                if (sparql.Contains("SELECT ?pt ?prop ?value", StringComparison.Ordinal))
                {
                    foreach (var (alias, uri) in AttributeUris)
                    {
                        if (row[alias] is null) continue;
                        projected.Add(new JsonObject
                        {
                            ["pt"] = row["pt"]!.DeepClone(),
                            ["prop"] = new JsonObject { ["type"] = "uri", ["value"] = uri },
                            ["value"] = row[alias]!.DeepClone(),
                        });
                    }
                }
                else if (sparql.Contains("SELECT ?pt ?devDt ?devId ?devName", StringComparison.Ordinal))
                {
                    if (row["devDt"] is null || row["devId"] is null) continue;
                    var device = new JsonObject
                    {
                        ["pt"] = row["pt"]!.DeepClone(),
                        ["devDt"] = row["devDt"]!.DeepClone(),
                        ["devId"] = row["devId"]!.DeepClone(),
                    };
                    if (row["devName"] is not null) device["devName"] = row["devName"]!.DeepClone();
                    projected.Add(device);
                }
                else
                {
                    projected.Add(new JsonObject
                    {
                        ["pt"] = row["pt"]!.DeepClone(),
                        ["ptId"] = row["ptId"]!.DeepClone(),
                    });
                }
            }

            var response = new JsonObject
            {
                ["results"] = new JsonObject { ["bindings"] = projected },
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/sparql-results+json"),
            };
        }
    }

    [Fact]
    public async Task ListGatewayPointList_MapsNativeAddressingControlSchemaAndDevice()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""pt"": {""type"":""uri"",""value"":""urn:point:PT001""},
      ""ptId"": {""type"":""literal"",""value"":""PT001""},
      ""ptName"": {""type"":""literal"",""value"":""Room Temp""},
      ""localId"": {""type"":""literal"",""value"":""LOCAL001""},
      ""devIdBac"": {""type"":""literal"",""value"":""BAC001""},
      ""objType"": {""type"":""literal"",""value"":""Analog-Input""},
      ""instNo"": {""type"":""literal"",""value"":""1001""},
      ""unit"": {""type"":""literal"",""value"":""C""},
      ""writable"": {""type"":""literal"",""value"":""true""},
      ""dataType"": {""type"":""literal"",""value"":""number""},
      ""minV"": {""type"":""literal"",""value"":""16""},
      ""maxV"": {""type"":""literal"",""value"":""30""},
      ""devDt"": {""type"":""uri"",""value"":""urn:dtid:dev1""},
      ""devId"": {""type"":""literal"",""value"":""DEV1""},
      ""devName"": {""type"":""literal"",""value"":""AC-1""} }
  ]}}");

        var entries = await db.ListGatewayPointList("GW001");

        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal("PT001", e.PointId);
        Assert.Equal("LOCAL001", e.LocalId);
        Assert.Equal("BAC001", e.BacnetDeviceId);
        Assert.Equal("Analog-Input", e.BacnetObjectType);
        Assert.Equal("1001", e.BacnetInstanceNo);
        Assert.Equal("C", e.Unit);
        Assert.True(e.Writable);
        Assert.Equal("number", e.DataType);
        Assert.Equal("16", e.MinValue);
        Assert.Equal("30", e.MaxValue);
        Assert.Equal("urn:dtid:dev1", e.DeviceDtId);
        Assert.Equal("DEV1", e.DeviceId);
        Assert.Equal("AC-1", e.DeviceName);
    }

    [Fact]
    public async Task ListGatewayPointList_ReturnsPointId_EvenWhenNativeAddressingMissing()
    {
        // A point with no localId/BACnet/control-schema must still appear (just with nulls).
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""pt"": {""type"":""uri"",""value"":""urn:point:PT002""},
      ""ptId"": {""type"":""literal"",""value"":""PT002""},
      ""ptName"": {""type"":""literal"",""value"":""CO2""} }
  ]}}");

        var entries = await db.ListGatewayPointList("GW001");

        Assert.Single(entries);
        Assert.Equal("PT002", entries[0].PointId);
        Assert.Null(entries[0].LocalId);
        Assert.Null(entries[0].BacnetObjectType);
        Assert.Null(entries[0].Writable);
    }

    [Fact]
    public async Task ListGatewayPointList_ReturnsEmpty_WhenGatewayOwnsNoPoints()
    {
        var db = BuildDb(@"{ ""results"": { ""bindings"": [] } }");
        var entries = await db.ListGatewayPointList("GW404");
        Assert.Empty(entries);
    }

    [Fact]
    public async Task ListGatewayPointList_ParsesWritableFalse()
    {
        var db = BuildDb(@"{
  ""results"": { ""bindings"": [
    { ""pt"": {""type"":""uri"",""value"":""urn:point:PT003""},
      ""ptId"": {""type"":""literal"",""value"":""PT003""},
      ""writable"": {""type"":""literal"",""value"":""false""} }
  ]}}");

        var entries = await db.ListGatewayPointList("GW001");
        Assert.False(entries[0].Writable);
    }

    // ── Multi-gateway isolation (#114/#224) ────────────────────────────────────
    // The tests above always stub a single canned response, so they cannot catch a regression where
    // the gatewayId FILTER is dropped/ignored. GatewayScopedFakeHandler hosts a shared twin dataset
    // covering two gateways and actually scopes its response by the gatewayId literal embedded in
    // each incoming SPARQL query — mirroring how a real triple store would apply the FILTER — so a
    // query for one gateway can never surface in the other's response.

    [Fact]
    public async Task ListGatewayPointList_TwoGatewaysInSharedDataset_EachSeesOnlyOwnPoints()
    {
        var handler = new GatewayScopedFakeHandler(new[]
        {
            ("GW001", "PT001", "Room Temp"),
            ("GW001", "PT002", "Room Humidity"),
            ("GW002", "PT101", "Damper Pos"),
        });
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var db = new OxiGraphDigitalTwinDatabase(client, cache);

        var gw001Entries = await db.ListGatewayPointList("GW001");
        var gw002Entries = await db.ListGatewayPointList("GW002");

        Assert.Equal(new[] { "PT001", "PT002" }, gw001Entries.Select(e => e.PointId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "PT101" }, gw002Entries.Select(e => e.PointId));
        Assert.DoesNotContain(gw001Entries, e => e.PointId == "PT101");
        Assert.DoesNotContain(gw002Entries, e => e.PointId is "PT001" or "PT002");
    }

    [Fact]
    public async Task ListGatewayPointList_UnregisteredGatewayId_ReturnsEmpty_EvenWhenOtherGatewaysOwnPoints()
    {
        var handler = new GatewayScopedFakeHandler(new[]
        {
            ("GW001", "PT001", "Room Temp"),
            ("GW002", "PT101", "Damper Pos"),
        });
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://oxigraph:7878");
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var db = new OxiGraphDigitalTwinDatabase(client, cache);

        var entries = await db.ListGatewayPointList("GW-UNKNOWN");

        Assert.Empty(entries);
    }

    /// <summary>
    /// Fakes the OxiGraph `/query` endpoint over a fixed (GatewayId, PointId, Name) dataset, filtering
    /// bindings by the gatewayId string literal embedded in <c>ListGatewayPointList</c>'s SPARQL query
    /// (the only place in that query where the gatewayId predicate is followed by a quoted literal
    /// rather than a variable) — a stand-in for the real triple store applying the FILTER.
    /// </summary>
    private sealed class GatewayScopedFakeHandler(IReadOnlyList<(string GatewayId, string PointId, string Name)> points)
        : HttpMessageHandler
    {
        private static readonly Regex GatewayIdLiteral = new("gatewayId>\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var encodedBody = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
            var sparql = WebUtility.UrlDecode(encodedBody);
            var match = GatewayIdLiteral.Match(sparql);
            var matched = match.Success
                ? points.Where(p => string.Equals(p.GatewayId, match.Groups[1].Value, StringComparison.Ordinal)).ToArray()
                : Array.Empty<(string, string, string)>();

            var bindings = string.Join(",", matched.Select(p =>
                $@"{{ ""pt"": {{""type"":""uri"",""value"":""urn:point:{p.Item2}""}}, ""ptId"": {{""type"":""literal"",""value"":""{p.Item2}""}}, ""ptName"": {{""type"":""literal"",""value"":""{p.Item3}""}} }}"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $@"{{ ""results"": {{ ""bindings"": [{bindings}] }} }}", Encoding.UTF8, "application/sparql-results+json"),
            };
        }
    }
}
