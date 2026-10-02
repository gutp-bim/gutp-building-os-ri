using System.ComponentModel.DataAnnotations;

namespace BuildingOS.Shared;

/// <summary>
/// Structured-attribute filters of the resource search (#454). Values within one list are ORed
/// (<c>deviceType ∈ {AHU, VAV}</c>); the lists are ANDed with each other and with q / type / tags. A
/// device's type also matches its points (a point carries the type of the equipment that owns it).
/// </summary>
public record ResourceAttributeFilter(
    IReadOnlyList<string> DeviceTypes,
    IReadOnlyList<string> PointTypes,
    IReadOnlyList<string> Units,
    IReadOnlyList<string> GatewayIds)
{
    public static readonly ResourceAttributeFilter None = new([], [], [], []);

    public bool IsEmpty => DeviceTypes.Count == 0 && PointTypes.Count == 0 && Units.Count == 0 && GatewayIds.Count == 0;
}

/// <summary>
/// One resource with the attributes the facets are built from. Internal plumbing between the twin
/// database and <c>AuthorizedTwinView</c> (which authorizes each row before counting); not an API shape.
/// </summary>
public class ResourceFacetRow
{
    public string Type { get; set; } = null!;
    public string DtId { get; set; } = null!;
    public string Id { get; set; } = null!;
    public string? DeviceType { get; set; }
    public string? PointType { get; set; }
    public string? Unit { get; set; }
    public string? GatewayId { get; set; }
}

/// <summary>A facet value and the number of readable resources in the current result that carry it.</summary>
public class FacetValueCount
{
    [Required]
    public string Value { get; set; } = null!;
    public int Count { get; set; }
}

/// <summary>
/// Facet counts over the resources a search matches (<c>GET /resources/facets</c>), restricted to what
/// the caller can read. A group's counts ignore that group's own selection (so the alternatives stay
/// visible and can be ORed in) and honour every other filter; <see cref="Total"/> honours all of them.
/// </summary>
public class ResourceFacets
{
    /// <summary>Number of readable resources the search matches.</summary>
    public int Total { get; set; }

    /// <summary>True when the twin held more matches than the scan cap, so counts are a lower bound.</summary>
    public bool Truncated { get; set; }

    /// <summary>building | floor | space | device | point</summary>
    [Required] public FacetValueCount[] Types { get; set; } = [];
    /// <summary>sbco:deviceType (of the device, or of the device owning the point).</summary>
    [Required] public FacetValueCount[] DeviceTypes { get; set; } = [];
    /// <summary>sbco:pointType (points only).</summary>
    [Required] public FacetValueCount[] PointTypes { get; set; } = [];
    /// <summary>sbco:unit (points only).</summary>
    [Required] public FacetValueCount[] Units { get; set; } = [];
    /// <summary>sbco:gatewayId (points only).</summary>
    [Required] public FacetValueCount[] Gateways { get; set; } = [];
}
