using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

public class Space
{
    /// <summary>
    /// Digital Twins ID ($dtId)
    /// </summary>
    [Required]
    public string DtId { get; set; }

    /// <summary>
    /// Business ID (spaceId)
    /// </summary>
    [Required]
    public string Id { get; set; }

    [Required]
    public string Name { get; set; }
}