namespace BuildingOS.Shared.Domain.TwinAdmin;

/// <summary>How an RDF import is applied to the default graph.</summary>
public enum TwinImportMode
{
    /// <summary>Add triples to the existing default graph (<c>ImportTurtleAsync</c>).</summary>
    Append,

    /// <summary>Replace the entire default graph (<c>ReplaceDefaultGraphAsync</c>) — destructive.</summary>
    Replace,
}

/// <summary>A gateway_id that the staged import maps across more than one building (uniqueness violation).</summary>
public sealed record GatewayCollision(string GatewayId, int BuildingCount);

/// <summary>
/// Why a staged resource is not reachable from a Building (#291). A point is reachable by either of
/// the two paths the twin actually models — the spatial chain
/// (Building →hasPart→ Level →hasPart→ Room ←locatedIn← EquipmentExt →hasPoint→ PointExt) or the
/// <c>sbco:floor</c> literal on EquipmentExt matched against a Level's <c>sbco:name</c>, which is what
/// the read side traverses — and each value names the outermost link missing on both. The wire values
/// are unchanged; their meaning is widened accordingly (see <see cref="NoRoom"/>).
/// </summary>
public static class TwinOrphanReasons
{
    /// <summary>No <c>sbco:EquipmentExt</c> links the point via <c>sbco:hasPoint</c>.</summary>
    public const string NoDevice = "no_device";

    /// <summary>
    /// The point has a device, but no device of it carries any spatial anchor at all — neither
    /// <c>sbco:locatedIn</c> an <c>sbco:Room</c> nor an <c>sbco:floor</c> literal. (Widened from
    /// "no Room": Room/<c>sbco:locatedIn</c> are optional in SBCO TTL, so a floor-literal twin is
    /// legitimate and must not be reported.)
    /// </summary>
    public const string NoRoom = "no_room";

    /// <summary>The device is anchored, but neither path reaches an <c>sbco:Building</c> via <c>sbco:hasPart</c>.</summary>
    public const string NoBuildingPath = "no_building_path";
}

/// <summary>
/// A staged resource the building hierarchy does not reach, and which link is missing
/// (<see cref="Reason"/> is one of <see cref="TwinOrphanReasons"/>).
/// </summary>
public sealed record TwinOrphanResource(string ResourceId, string Reason);

/// <summary>
/// Pre-apply analysis of an RDF import, computed by staging the Turtle in a temporary named graph
/// (#322): triple/gateway counts, any gateway_id→multiple-building collisions, and the points the
/// building hierarchy does not reach (#291). <see cref="Valid"/> is false when either exists;
/// applying anyway is blocked by the controller (orphans only, and only on an explicit override).
/// <c>Orphans</c> is a capped sample for display, so it may be shorter than <c>OrphanCount</c>.
/// </summary>
public sealed record TwinImportPreview(
    long TripleCount,
    int GatewayCount,
    IReadOnlyList<GatewayCollision> Collisions,
    int OrphanCount,
    IReadOnlyList<TwinOrphanResource> Orphans)
{
    public bool Valid => Collisions.Count == 0 && OrphanCount == 0;
}

/// <summary>Result of a read-only SPARQL query: columns + rows (capped) + timing.</summary>
public sealed record SparqlQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows,
    int RowCount,
    bool Truncated,
    long ElapsedMs);
