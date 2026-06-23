using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

public class PointDetail
{
    [Required]
    public Point Point { get; set; }
    public Floor? Floor { get; set; }
    public Space? Space { get; set; }
    public Device? Device { get; set; }
    public ControlSchema? ControlSchema { get; set; }
}