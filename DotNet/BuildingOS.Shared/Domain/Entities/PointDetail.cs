using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

public class PointDetail
{
    [Required]
    public Point Point { get; set; } = null!;
    /// <summary>
    /// The building the point is placed in, by topology (#547). Null when the twin does not place it
    /// in one. Carries the dtId so a client can scope building-level reads (e.g. the data-health
    /// list) to it; <see cref="Device.BuildingName"/> remains for display.
    /// </summary>
    public Building? Building { get; set; }
    public Floor? Floor { get; set; }
    public Space? Space { get; set; }
    public Device? Device { get; set; }
    public ControlSchema? ControlSchema { get; set; }
}