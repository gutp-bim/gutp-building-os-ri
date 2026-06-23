using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

public class Building
{
    /// <summary>
    /// Digital Twins ID ($dtId)
    /// </summary>
    [Required]
    public string DtId { get; set; }

    /// <summary>
    /// Business ID (buildingId)
    /// </summary>
    [Required]
    public string Id { get; set; }

    [Required]
    public string Name { get; set; }
}