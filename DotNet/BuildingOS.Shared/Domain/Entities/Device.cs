using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

public class Device
{
    /// <summary>
    /// Digital Twins ID ($dtId)
    /// </summary>
    [Required]
    public string DtId { get; set; }

    /// <summary>
    /// Business ID (deviceId)
    /// </summary>
    [Required]
    public string Id { get; set; }

    [Required]
    public string Name { get; set; }

    public string? BuildingName { get; set; }
    public int? FloorNumber { get; set; }
    public string? Owner { get; set; }
    public string? Site { get; set; }
    public string? Supplier { get; set; }
    public string? GatewayId { get; set; }
    public string? DeviceType { get; set; }
}