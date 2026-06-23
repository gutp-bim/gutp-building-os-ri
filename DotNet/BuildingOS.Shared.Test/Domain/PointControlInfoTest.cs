using BuildingOS.Shared.Domain;
using System.Text.Json;
using Xunit;

namespace BuildingOS.Shared.Test.Domain;

public class PointControlInfoTest
{
    [Fact]
    public void PointId_IsIncluded_InJsonSerialization()
    {
        var info = new PointControlInfo
        {
            id = Guid.NewGuid(),
            PointId = "building-os/device-1/temp",
            Type = "Hono",
            Body = "{}",
        };

        var json = JsonSerializer.Serialize(info);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("building-os/device-1/temp", doc.RootElement.GetProperty("PointId").GetString());
    }

    [Fact]
    public void PointId_RoundTrips_ThroughJsonDeserialization()
    {
        var original = new PointControlInfo
        {
            id = Guid.NewGuid(),
            PointId = "urn:point:abc123",
            Type = "Hono",
            Body = "{}",
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<PointControlInfo>(json);

        Assert.Equal(original.PointId, restored!.PointId);
    }

    [Fact]
    public void PointId_IsNull_WhenAbsentInLegacyJson()
    {
        // Old messages (before #133) have no PointId — must deserialize without error
        var legacyJson = """{"id":"00000000-0000-0000-0000-000000000001","Type":"BACnet","Body":"{}"}""";
        var info = JsonSerializer.Deserialize<PointControlInfo>(legacyJson);
        Assert.Null(info!.PointId);
    }

    [Fact]
    public void DeviceControlType_Hono_IsDefinedAsConstant()
    {
        Assert.Equal("Hono", DeviceControlType.Hono);
    }

    [Fact]
    public void DeviceControlType_Kandt_IsDefinedAsConstant()
    {
        // The IoT Hub direct-method egress is the Kandt gateway, not generic "BACnet".
        Assert.Equal("Kandt", DeviceControlType.Kandt);
    }
}
