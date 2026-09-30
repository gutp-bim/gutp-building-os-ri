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
/// Why a staged resource is not reachable from a Building (#291). The twin is traversed by topology
/// only, so a point is reachable by one of two chains — the Room spatial chain
/// (Building →hasPart→ Level →hasPart→ Room ←locatedIn← EquipmentExt →hasPoint→ PointExt) or the
/// direct-Level chain (Building →hasPart→ Level ←locatedIn← EquipmentExt →hasPoint→ PointExt). The
/// <c>sbco:floor</c> literal is metadata and places nothing (see <see cref="FloorLiteralOnly"/>).
/// </summary>
public static class TwinOrphanReasons
{
    /// <summary>No <c>sbco:EquipmentExt</c> links the point via <c>sbco:hasPoint</c>.</summary>
    public const string NoDevice = "no_device";

    /// <summary>
    /// The point has a device, but no device of it carries any spatial anchor at all — no
    /// <c>sbco:locatedIn</c> an <c>sbco:Room</c> or <c>sbco:Level</c>. (Widened from "no Room": a Room
    /// is optional in SBCO TTL, so a direct-Level twin is legitimate and is not reported.)
    /// </summary>
    public const string NoRoom = "no_room";

    /// <summary>
    /// The point's device names a Level only through the <c>sbco:floor</c> literal and has no
    /// <c>sbco:locatedIn</c> at all (one pointing at an untyped node is <see cref="NoRoom"/>). The literal is metadata, not a relationship, so the device is placed
    /// nowhere: the twin's builder must emit <c>sbco:locatedIn</c> to the Room or Level.
    /// </summary>
    public const string FloorLiteralOnly = "floor_literal_only";

    /// <summary>The device is anchored, but no supported path reaches an <c>sbco:Building</c> via <c>sbco:hasPart</c>.</summary>
    public const string NoBuildingPath = "no_building_path";
}

/// <summary>
/// A staged resource the building hierarchy does not reach, and which link is missing
/// (<see cref="Reason"/> is one of <see cref="TwinOrphanReasons"/>).
/// </summary>
public sealed record TwinOrphanResource(string ResourceId, string Reason);

/// <summary>
/// Why a writable point's <c>bos:</c> control schema could not be resolved to a usable shape (#336).
/// Both reasons converge to the same silent fail-open at control time (<c>ControlValueValidator</c>
/// skips validation) — this is the detection side, run at twin-ingestion time instead of the control
/// hot path, so the twin's author sees it instead of it staying invisible forever.
/// </summary>
public static class ControlSchemaIssueReasons
{
    /// <summary>A writable point carries no <c>bos:dataType</c> triple at all.</summary>
    public const string MissingDataType = "missing_datatype";

    /// <summary>
    /// A writable point declares <c>bos:dataType "enum"</c> but its <c>bos:enumLabels</c> is missing,
    /// empty, or not a JSON object — <c>ControlValueValidator.ParseAllowedCodes</c> would silently
    /// treat this as "no allowed set" and validate permissively.
    /// </summary>
    public const string MalformedEnumLabels = "malformed_enum_labels";

    /// <summary>No control schema resolved for the point at all (control time only, #481).</summary>
    public const string NoSchema = "no_schema";

    /// <summary>
    /// <c>bos:dataType</c> is present but not one the validator understands (<c>boolean</c> /
    /// <c>enum</c> / <c>number</c>), so no value can be checked (control time only, #481).
    /// </summary>
    public const string UnknownDataType = "unknown_datatype";
}

/// <summary>
/// A writable point whose <c>bos:</c> control schema is missing or unusable
/// (<see cref="Reason"/> is one of <see cref="ControlSchemaIssueReasons"/>).
/// </summary>
public sealed record TwinControlSchemaIssue(string PointId, string Reason);

/// <summary>
/// Pre-apply analysis of an RDF import, computed by staging the Turtle in a temporary named graph
/// (#322): triple/gateway counts, any gateway_id→multiple-building collisions, and the points the
/// building hierarchy does not reach (#291). <see cref="Valid"/> is false when either exists;
/// applying anyway is blocked by the controller (orphans only, and only on an explicit override).
/// <c>Orphans</c> is a capped sample for display, so it may be shorter than <c>OrphanCount</c>.
/// <c>ControlSchemaIssues</c> (#336) is observation-only — it never affects <see cref="Valid"/> or
/// blocks apply, matching the control path's own fail-open design.
/// </summary>
public sealed record TwinImportPreview(
    long TripleCount,
    int GatewayCount,
    IReadOnlyList<GatewayCollision> Collisions,
    int OrphanCount,
    IReadOnlyList<TwinOrphanResource> Orphans,
    int ControlSchemaIssueCount,
    IReadOnlyList<TwinControlSchemaIssue> ControlSchemaIssues)
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
