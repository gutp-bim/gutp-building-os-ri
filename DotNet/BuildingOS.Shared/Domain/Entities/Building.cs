using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

public class Building
{
    /// <summary>
    /// Digital Twins ID ($dtId)
    /// </summary>
    [Required]
    public string DtId { get; set; } = null!;

    /// <summary>
    /// Business ID (buildingId)
    /// </summary>
    [Required]
    public string Id { get; set; } = null!;

    [Required]
    public string Name { get; set; } = null!;

    public Dictionary<string, string> Identifiers { get; set; } = new();
    public Dictionary<string, bool> CustomTags { get; set; } = new();
}