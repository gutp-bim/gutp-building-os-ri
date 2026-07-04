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
    public string? Panel { get; set; }
    public string? Labels { get; set; }
    public float? Scale { get; set; }
    public string? InstallationArea { get; set; }
    public string? Unit { get; set; }
    public float? Interval { get; set; }
    public int? InstanceNoBacnet { get; set; }
    public string? ObjectTypeBacnet { get; set; }
    public string? DeviceIdBacnet { get; set; }

    public string? RowDataString { get; set; }

    public Dictionary<string, string> Identifiers { get; set; } = new();
    public Dictionary<string, bool> CustomTags { get; set; } = new();
}