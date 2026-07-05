using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

/// <summary>
/// A single match from the cross-resource search (/resources/search). Carries just enough to render
/// a result row and jump to the resource in the tree: its kind, the digital-twin id (= node URI),
/// the business id, the display name, and (when known) the owning building's dtId for tree expansion.
/// </summary>
public class ResourceSearchHit
{
    /// <summary>building | floor | space | device | point</summary>
    [Required]
    public string Type { get; set; } = null!;

    /// <summary>Digital Twin ID (node URI). For point this differs from <see cref="Id"/>.</summary>
    [Required]
    public string DtId { get; set; } = null!;

    /// <summary>Business ID. For point this is the pointId used for authorization.</summary>
    [Required]
    public string Id { get; set; } = null!;

    [Required]
    public string Name { get; set; } = null!;

    /// <summary>Owning building's dtId when resolvable; null otherwise.</summary>
    public string? BuildingDtId { get; set; }
}
