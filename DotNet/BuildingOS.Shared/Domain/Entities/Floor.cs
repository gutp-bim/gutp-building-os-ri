using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

public class Floor
{
    /// <summary>
    /// Digital Twins ID ($dtId)
    /// </summary>
    [Required]
    public string DtId { get; set; }

    /// <summary>
    /// Business ID (floorId)
    /// </summary>
    [Required]
    public string Id { get; set; }

    [Required]
    public string Name { get; set; }

    public Dictionary<string, string> Identifiers { get; set; } = new();
    public Dictionary<string, bool> CustomTags { get; set; } = new();
}