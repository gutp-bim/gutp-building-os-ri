using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

public class Point
{
    /// <summary>
    /// Digital Twins ID ($dtId)
    /// </summary>
    [Required]
    public string DtId { get; set; } = null!;

    /// <summary>
    /// Business ID (pointId) - formerly PointId property
    /// </summary>
    [Required]
    public string Id { get; set; } = null!;

    [Required]
    public string Name { get; set; } = null!;

    public string? Specification { get; set; }
    public string? Type { get; set; }
    public bool? Writable { get; set; }
    public string? GatewayName { get; set; }
    public int? MinPresValue { get; set; }
    public int? MaxPresValue { get; set; }
    public string? TargetArea { get; set; }
    public float? Scale { get; set; }
    public string? InstallationArea { get; set; }
    public string? Unit { get; set; }
    public float? Interval { get; set; }

    /// <summary>Protocol-native point address (for MQTT, the concrete topic).</summary>
    public string? LocalId { get; set; }

    /// <summary>Resolved collection protocol (explicit twin value, native fields, or local-id shape).</summary>
    public string? Protocol { get; set; }

    // Opt-in per-point alarm thresholds (#158 Phase 2a, ADR-0005). Distinct from Min/MaxPresValue
    // (legacy BACnet raw bounds) and the ControlSchema control-write range: these are the
    // normal-operation value range. alarm* = critical (outer) limits, warn* = inner limits; all optional.
    public float? AlarmHigh { get; set; }
    public float? AlarmLow { get; set; }
    public float? WarnHigh { get; set; }
    public float? WarnLow { get; set; }
    public int? InstanceNoBacnet { get; set; }
    public string? ObjectTypeBacnet { get; set; }
    public string? DeviceIdBacnet { get; set; }

    public Dictionary<string, string> Identifiers { get; set; } = new();
    public Dictionary<string, bool> CustomTags { get; set; } = new();
}
