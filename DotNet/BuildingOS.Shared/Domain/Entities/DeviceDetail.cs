using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

public class DeviceDetail
{
    [Required]
    public Device Device { get; set; } = null!;
    public Floor? Floor { get; set; }
    public Space? Space { get; set; }
}