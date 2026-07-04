using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

public class Device
{
    /// <summary>
    /// Digital Twins ID ($dtId)
    /// </summary>
    [Required]
    public string DtId { get; set; } = null!;

    /// <summary>
    /// Business ID (deviceId)
    /// </summary>
    [Required]
    public string Id { get; set; } = null!;

    [Required]
    public string Name { get; set; } = null!;

    public string? BuildingName { get; set; }
    public int? FloorNumber { get; set; }
    public string? Owner { get; set; }
    public string? Site { get; set; }
    public string? Supplier { get; set; }
    public string? GatewayId { get; set; }
    public string? DeviceType { get; set; }

    public Dictionary<string, string> Identifiers { get; set; } = new();
    public Dictionary<string, bool> CustomTags { get; set; } = new();
}