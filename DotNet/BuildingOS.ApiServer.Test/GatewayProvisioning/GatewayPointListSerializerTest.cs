using BuildingOS.Shared;
using BuildingOs.ApiServer.GatewayProvisioning;

namespace BuildingOS.ApiServer.Test.GatewayProvisioning;

/// <summary>
/// Pure tests for the gateway point-list canonical serialization + content-hash ETag (#224 / U2).
/// </summary>
public class GatewayPointListSerializerTest
{
    private static GatewayPointEntry P(string id, string? unit = null, bool? writable = null) =>
        new() { PointId = id, Unit = unit, Writable = writable };

    [Fact]
    public void Etag_IsOrderIndependent()
    {
        var a = new[] { P("PT001"), P("PT002"), P("PT003") };
        var b = new[] { P("PT003"), P("PT001"), P("PT002") };

        Assert.Equal(PointListEtag.Compute(a), PointListEtag.Compute(b));
    }

    [Fact]
    public void Etag_ChangesWhenAnyFieldChanges()
    {
        var baseline = new[] { P("PT001", unit: "C") };
        var changed = new[] { P("PT001", unit: "F") };

        Assert.NotEqual(PointListEtag.Compute(baseline), PointListEtag.Compute(changed));
    }

    [Fact]
    public void Etag_ChangesWhenPointAddedOrRemoved()
    {
        var one = new[] { P("PT001") };
        var two = new[] { P("PT001"), P("PT002") };

        Assert.NotEqual(PointListEtag.Compute(one), PointListEtag.Compute(two));
    }

    [Fact]
    public void Etag_IsStableForEmptyList()
    {
        Assert.Equal(
            PointListEtag.Compute(System.Array.Empty<GatewayPointEntry>()),
            PointListEtag.Compute(System.Array.Empty<GatewayPointEntry>()));
    }

    [Fact]
    public void Etag_HasStrongFormat()
    {
        var etag = PointListEtag.Compute(new[] { P("PT001") });
        // Quoted strong validator, sha256: prefix → safe for the ETag header.
        Assert.StartsWith("\"sha256:", etag);
        Assert.EndsWith("\"", etag);
    }

    [Fact]
    public void Serialize_OrdersByPointId()
    {
        var json = GatewayPointListSerializer.SerializePoints(new[] { P("PT003"), P("PT001"), P("PT002") });
        var i1 = json.IndexOf("PT001", System.StringComparison.Ordinal);
        var i2 = json.IndexOf("PT002", System.StringComparison.Ordinal);
        var i3 = json.IndexOf("PT003", System.StringComparison.Ordinal);
        Assert.True(i1 < i2 && i2 < i3);
    }
}
