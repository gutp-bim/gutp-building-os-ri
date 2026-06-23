using BuildingOS.Shared;

namespace BuildingOs.ApiServer.GatewayProvisioning;

/// <summary>Gateway point-list export response (#224). <see cref="Revision"/> equals the ETag.</summary>
public sealed class GatewayPointListResponse
{
    public string GatewayId { get; set; } = "";
    public string Revision { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public GatewayPointDto[] Points { get; set; } = [];
}

/// <summary>
/// Diff response for `?since={etag}` (#224/diff). When <see cref="Full"/> is true the snapshot for
/// `since` was not retained and <see cref="Points"/> carries the complete list (client should replace);
/// otherwise <see cref="Added"/>/<see cref="Removed"/>/<see cref="Changed"/> carry the delta.
/// </summary>
public sealed class GatewayPointListDiffResponse
{
    public string GatewayId { get; set; } = "";
    public string Revision { get; set; } = "";   // current ETag
    public string Since { get; set; } = "";       // requested base ETag
    public bool Full { get; set; }
    public GatewayPointDto[] Added { get; set; } = [];
    public string[] Removed { get; set; } = [];   // pointIds
    public GatewayPointDto[] Changed { get; set; } = [];
    public GatewayPointDto[] Points { get; set; } = []; // populated only when Full == true
}

public sealed class GatewayPointDto
{
    public string PointId { get; set; } = "";
    public string? LocalId { get; set; }
    public NativeAddressingDto? Native { get; set; }
    public string? Unit { get; set; }
    public bool? Writable { get; set; }
    public ControlSchemaDto? ControlSchema { get; set; }
    public DeviceRefDto? Device { get; set; }

    public static GatewayPointDto From(GatewayPointEntry e) => new()
    {
        PointId = e.PointId,
        LocalId = e.LocalId,
        Native = NativeAddressingDto.From(e),
        Unit = e.Unit,
        Writable = e.Writable,
        ControlSchema = ControlSchemaDto.From(e),
        Device = DeviceRefDto.From(e),
    };
}

public sealed class NativeAddressingDto
{
    public string Protocol { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? ObjectType { get; set; }
    public string? InstanceNo { get; set; }

    public static NativeAddressingDto? From(GatewayPointEntry e)
    {
        if (e.BacnetDeviceId is null && e.BacnetObjectType is null && e.BacnetInstanceNo is null)
            return null;
        return new NativeAddressingDto
        {
            Protocol = "bacnet",
            DeviceId = e.BacnetDeviceId,
            ObjectType = e.BacnetObjectType,
            InstanceNo = e.BacnetInstanceNo,
        };
    }
}

public sealed class ControlSchemaDto
{
    public string? DataType { get; set; }
    public string? MinValue { get; set; }
    public string? MaxValue { get; set; }
    public string? EnumLabels { get; set; }

    public static ControlSchemaDto? From(GatewayPointEntry e)
    {
        if (e.DataType is null && e.MinValue is null && e.MaxValue is null && e.EnumLabels is null)
            return null;
        return new ControlSchemaDto
        {
            DataType = e.DataType,
            MinValue = e.MinValue,
            MaxValue = e.MaxValue,
            EnumLabels = e.EnumLabels,
        };
    }
}

public sealed class DeviceRefDto
{
    public string? DtId { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }

    public static DeviceRefDto? From(GatewayPointEntry e)
    {
        if (e.DeviceId is null && e.DeviceDtId is null && e.DeviceName is null) return null;
        return new DeviceRefDto { DtId = e.DeviceDtId, Id = e.DeviceId, Name = e.DeviceName };
    }
}
