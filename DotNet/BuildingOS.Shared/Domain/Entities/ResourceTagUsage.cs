using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

/// <summary>
/// One (resource, customTag) pair from the twin — the unit the tag suggestion endpoint authorizes and
/// counts. Internal plumbing between the twin database and <c>AuthorizedTwinView</c>; not an API shape.
/// </summary>
public class ResourceTagUsage
{
    /// <summary>building | floor | space | device | point</summary>
    public string Type { get; set; } = null!;
    public string DtId { get; set; } = null!;
    /// <summary>Business id (what read grants are checked against).</summary>
    public string Id { get; set; } = null!;
    public string Tag { get; set; } = null!;
}

/// <summary>A customTags key with the number of readable resources carrying it (<c>GET /resources/tags</c>).</summary>
public class ResourceTagCount
{
    [Required]
    public string Tag { get; set; } = null!;

    /// <summary>Distinct resources, within the caller's read authorization, tagged with <see cref="Tag"/>.</summary>
    public int Count { get; set; }
}
