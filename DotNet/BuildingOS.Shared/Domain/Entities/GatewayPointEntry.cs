namespace BuildingOS.Shared;

/// <summary>
/// One row of a gateway-scoped point list export (#224). Carries the canonical <see cref="PointId"/>
/// plus whatever native addressing / control metadata the twin holds for that point, so a gateway can
/// resolve protocol-native addressing locally and know unit / writability / control bounds / device.
/// Native fields are optional — a point with no native addressing still appears (with nulls).
/// </summary>
public class GatewayPointEntry
{
    /// <summary>sbco:id — the shared-point-list identity carried on the wire.</summary>
    public string PointId { get; set; } = "";

    /// <summary>sbco:localId — generic local key (e.g. MQTT/Hono tenant/deviceId), when present.</summary>
    public string? LocalId { get; set; }

    // BACnet native addressing (sbco:deviceIdBacnet / objectTypeBacnet / instanceNoBacnet), when present.
    public string? BacnetDeviceId { get; set; }
    public string? BacnetObjectType { get; set; }
    public string? BacnetInstanceNo { get; set; }

    public string? Unit { get; set; }
    public bool? Writable { get; set; }

    // Control schema (bos:dataType / minValue / maxValue / enumLabels).
    public string? DataType { get; set; }
    public string? MinValue { get; set; }
    public string? MaxValue { get; set; }
    public string? EnumLabels { get; set; }

    // Owning device grouping.
    public string? DeviceDtId { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
}
