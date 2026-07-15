using System.Text.Json;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.ControlRouting;

namespace BuildingOS.Shared.Test.Infrastructure.ControlRouting;

public class ControlTypeResolverTest
{
    private static ControlTypeResolver Build(Dictionary<string, string>? map = null, string defaultType = "hono")
        => new(new ConfigGatewayConnectionRegistry(
            map ?? new(), defaultType,
            new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            new Dictionary<string, IReadOnlyDictionary<string, string>>()));

    private static Point WritablePoint(string? devIdBacnet = null, string? objType = null, int? instanceNo = null)
        => new()
        {
            DtId = "urn:pt:1", Id = "PT1", Name = "SetTemp", Writable = true,
            DeviceIdBacnet = devIdBacnet, ObjectTypeBacnet = objType, InstanceNoBacnet = instanceNo,
        };

    private static Device Gateway(string gatewayId)
        => new() { DtId = "urn:dev:1", Id = "D1", Name = "AC", GatewayId = gatewayId };

    // ── connectionType → ControlType ────────────────────────────────────────

    [Fact]
    public void Resolve_MapsBacnetSimConnectionType_ToBacnetSimControlType()
    {
        var resolver = Build(new() { ["gw-sim"] = "bacnet-sim" });
        var dispatch = resolver.Resolve(
            WritablePoint(devIdBacnet: "1001", objType: "1", instanceNo: 5), Gateway("gw-sim"), 21.5);

        Assert.NotNull(dispatch);
        Assert.Equal(DeviceControlType.BacnetSim, dispatch!.ControlType);
        Assert.Equal("gw-sim", dispatch.GatewayId);
    }

    [Fact]
    public void Resolve_DefaultsToHono_ForUnmappedGateway()
    {
        // No-regression: unmapped gateway keeps the previous Hono body shape { "value": v }.
        var resolver = Build();
        var dispatch = resolver.Resolve(WritablePoint(), Gateway("some-gw"), 21.5);

        Assert.NotNull(dispatch);
        Assert.Equal(DeviceControlType.Hono, dispatch!.ControlType);
        using var doc = JsonDocument.Parse(dispatch.Body);
        Assert.Equal(21.5, doc.RootElement.GetProperty("value").GetDouble());
    }

    [Fact]
    public void Resolve_DefaultsToHono_WhenDeviceIsNull()
    {
        var resolver = Build();
        var dispatch = resolver.Resolve(WritablePoint(), device: null, 1.0);
        Assert.Equal(DeviceControlType.Hono, dispatch!.ControlType);
    }

    [Fact]
    public void Resolve_MapsSimulatedConnectionType_ToSimulatedControlType()
    {
        var resolver = Build(new() { ["gw-sim"] = "simulated" });
        var dispatch = resolver.Resolve(WritablePoint(), Gateway("gw-sim"), 1.0);

        Assert.NotNull(dispatch);
        Assert.Equal(DeviceControlType.Simulated, dispatch!.ControlType);
        Assert.Equal("gw-sim", dispatch.GatewayId);
        using var doc = JsonDocument.Parse(dispatch.Body);
        Assert.Equal(1.0, doc.RootElement.GetProperty("value").GetDouble());
    }

    // ── BacnetSim body builder (point-id canonical, #181) ───────────────────

    [Fact]
    public void Resolve_BuildsBacnetSimBody_WithValueOnly()
    {
        // The gateway resolves point_id → BACnet object/instance from the shared point list, so the
        // command body carries only the value; point_id rides on PointControlInfo.
        var resolver = Build(new() { ["gw-sim"] = "bacnet-sim" });
        var dispatch = resolver.Resolve(WritablePoint(), Gateway("gw-sim"), 23.0);

        Assert.NotNull(dispatch);
        Assert.Equal(DeviceControlType.BacnetSim, dispatch!.ControlType);
        using var doc = JsonDocument.Parse(dispatch.Body);
        Assert.Equal(23.0, doc.RootElement.GetProperty("value").GetDouble());
    }

    // ── abnormal: not controllable → null ───────────────────────────────────

    [Fact]
    public void Resolve_ReturnsNull_WhenPointNotWritable()
    {
        var resolver = Build();
        var point = WritablePoint();
        point.Writable = false;
        Assert.Null(resolver.Resolve(point, Gateway("gw"), 1.0));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive_ForConnectionType()
    {
        // Config typo tolerance: "Bacnet-Sim" still routes to BacnetSim.
        var resolver = Build(new() { ["gw-sim"] = "Bacnet-Sim" });
        var dispatch = resolver.Resolve(WritablePoint(), Gateway("gw-sim"), 1.0);
        Assert.Equal(DeviceControlType.BacnetSim, dispatch!.ControlType);
    }

    [Fact]
    public void Resolve_ReturnsNull_ForBacnetSimWithoutGateway()
    {
        // BacnetSim must have a gatewayId to be deliverable via the bridge; default→bacnet-sim with
        // a null device gateway is a misconfiguration, not controllable.
        var resolver = Build(defaultType: "bacnet-sim");
        var dispatch = resolver.Resolve(WritablePoint(), device: null, 1.0);
        Assert.Null(dispatch);
    }

    [Fact]
    public void Resolve_ReturnsNull_ForUnsupportedConnectionType()
    {
        // e.g. a gateway configured as "kandt" — API-side body building for kandt is not wired in this slice.
        var resolver = Build(new() { ["gw-x"] = "kandt" });
        Assert.Null(resolver.Resolve(WritablePoint(), Gateway("gw-x"), 1.0));
    }
}
