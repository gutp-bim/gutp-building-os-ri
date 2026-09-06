using System.Text.Json;
using BuildingOS.Shared;

namespace BuildingOS.Shared.Test.Domain;

/// <summary>
/// Guards the MQTT-flavored shared fixture added for #410: <c>fixtures/e2e/twin-mqtt.ttl</c> /
/// <c>pointlist-mqtt.csv</c> / <c>pointlist-mqtt.json</c> exist to let MQTT-connector gateways (whose
/// <c>local_id</c> is a topic, not an OPC-UA nodeId) reuse the same "3 repos point at the same
/// (gateway_id, point_id)" cross-repo guarantee as the BACnet/OPC-UA fixture.
///
/// The one thing that would silently break that guarantee is a fixture whose points don't actually
/// resolve to the "mqtt" protocol — e.g. a stray BACnet native field reintroduced by a future edit
/// would flip <see cref="GatewayPointProtocolResolver"/>'s resolution to "bacnet" regardless of the
/// localId shape (it is checked first), and nothing else in this repo would notice.
/// </summary>
public class ShippedMqttTwinFixtureTest
{
    private const int ExpectedPointCount = 30;
    private const int ExpectedWritableCount = 7;
    private const string ExpectedGatewayId = "GW-SOS-MQTT-001";

    [Fact]
    public void PointListCsv_Header_MatchesTheCanonicalBacnetFixtureHeader()
    {
        // The README documents pointlist.csv's header as the canonical standard-pointlist column
        // set; the MQTT fixture must reuse it verbatim so both fixtures stay drop-in compatible with
        // the same consumers.
        var canonicalHeader = File.ReadLines(FixturePath("pointlist.csv")).First();
        var mqttHeader = File.ReadLines(FixturePath("pointlist-mqtt.csv")).First();

        Assert.Equal(canonicalHeader, mqttHeader);
    }

    [Fact]
    public void PointListCsv_HasThirtyRows_WithSevenWritable()
    {
        var rows = ReadCsvRows();

        Assert.Equal(ExpectedPointCount, rows.Count);
        Assert.Equal(ExpectedWritableCount, rows.Count(r => r["writable"] == "true"));
        Assert.Equal(ExpectedPointCount, rows.Select(r => r["point_id"]).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rows, r => Assert.Equal(ExpectedGatewayId, r["gateway_id"]));
    }

    [Fact]
    public void PointListCsv_EveryRow_ResolvesToMqttProtocol()
    {
        // The real regression guard: run the fixture's rows through the same resolver nexus-gateway's
        // csv.go mirrors (#224), instead of just regexing for a "/" in local_id. A row that carries a
        // stray BACnet native field would resolve to "bacnet" even with an MQTT-shaped local_id.
        var rows = ReadCsvRows();

        Assert.All(rows, r =>
        {
            var resolved = GatewayPointProtocolResolver.Resolve(
                explicitProtocol: null,
                bacnetDeviceId: Empty(r["device_id_bacnet"]),
                bacnetObjectType: Empty(r["object_type_bacnet"]),
                bacnetInstanceNo: Empty(r["instance_no_bacnet"]),
                localId: r["local_id"]);

            Assert.Equal("mqtt", resolved);
        });
    }

    [Fact]
    public void TwinTtl_DeclaresTheSamePointSet_AsThePointListCsv()
    {
        var ttl = File.ReadAllText(FixturePath("twin-mqtt.ttl"));
        var csvPointIds = ReadCsvRows().Select(r => r["point_id"]).OrderBy(v => v, StringComparer.Ordinal).ToList();

        var ttlPointIds = System.Text.RegularExpressions.Regex
            .Matches(ttl, "sbr:(MQTT-PT-\\d+) a sbco:PointExt")
            .Select(m => m.Groups[1].Value)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(csvPointIds, ttlPointIds);
        Assert.Contains($"sbco:gatewayId \"{ExpectedGatewayId}\"", ttl);
    }

    [Fact]
    public void PointListJson_MatchesTheCsvPointCountAndWritableCount_AndOmitsNative()
    {
        // fixtures/e2e/pointlist.json is the expected GET /gateways/{id}/pointlist response for the
        // same dataset. For a point with no BACnet fields, NativeAddressingDto.From(...) returns null
        // (GatewayPointListResponse.cs) — the wire contract this snapshot must match.
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath("pointlist-mqtt.json")));
        var root = doc.RootElement;
        var points = root.GetProperty("points").EnumerateArray().ToList();

        Assert.Equal(ExpectedGatewayId, root.GetProperty("gatewayId").GetString());
        Assert.Equal(ExpectedPointCount, points.Count);
        Assert.Equal(ExpectedWritableCount, points.Count(p => p.GetProperty("writable").GetBoolean()));
        Assert.All(points, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("native").ValueKind));

        var csvPointIds = ReadCsvRows().Select(r => r["point_id"]).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var jsonPointIds = points.Select(p => p.GetProperty("pointId").GetString()!)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(csvPointIds, jsonPointIds);
    }

    private static string? Empty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static List<Dictionary<string, string>> ReadCsvRows()
    {
        var lines = File.ReadAllLines(FixturePath("pointlist-mqtt.csv"));
        var header = lines[0].Split(',');
        return lines.Skip(1)
            .Where(l => l.Length > 0)
            .Select(line =>
            {
                var cells = line.Split(',');
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < header.Length; i++)
                    row[header[i]] = i < cells.Length ? cells[i] : string.Empty;
                return row;
            })
            .ToList();
    }

    /// <summary>
    /// Walks up from the test binary to the repository root (identified by the fixtures directory),
    /// so the test does not depend on the build output layout.
    /// </summary>
    private static string FixturePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures", "e2e", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate fixtures/e2e/{fileName} above {AppContext.BaseDirectory}");
    }
}
