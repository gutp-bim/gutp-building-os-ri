using BuildingOS.Shared.Entities;

namespace BuildingOS.Shared.Infrastructure;

public interface IDigitalTwinDatabase
{
    public Task<Building[]> ListBuildings();
    public Task<Building?> GetBuilding(string dtId);
    public Task<Floor[]> ListFloors(string? buildingDtId);
    public Task<Floor?> GetFloor(string dtId);
    public Task<Space[]> ListSpaces(string? floorDtId);
    public Task<Space?> GetSpace(string dtId);

    /// <summary>
    /// Rooms directly adjacent to <paramref name="spaceDtId"/> (#440, BOT <c>bot:adjacentZone</c>
    /// materialized to the <c>bos:adjacentZone</c> canonical form at ingest). Adjacency is symmetric
    /// and both directions are written at ingest, so this is a one-direction SELECT — no UNION.
    /// Only <c>sbco:Room</c> neighbours are returned: BOT models adjacency between any two zones,
    /// but this read path answers "which rooms are next door". Empty when nothing is adjacent, and
    /// also when the room does not exist — callers that must tell those apart check the room first.
    /// </summary>
    public Task<Space[]> ListAdjacentSpaces(string spaceDtId);

    public Task<Device[]> ListDevices(string? spaceDtId);
    /// <summary>
    /// Equipment located directly on the Level (<c>sbco:locatedIn &lt;floorDtId&gt;</c>, no Room) — the
    /// placement the floor → room → device walk never reaches (#544). Empty if the dtId is not a Level.
    /// </summary>
    public Task<Device[]> ListFloorDevices(string floorDtId, CancellationToken ct = default);
    public Task<Device?> GetDevice(string dtId);
    public Task<Point[]> ListPoints(string? deviceDtId);
    public Task<Point?> GetPoint(string pointId);
    public Task<PointDetail?> GetPointDetailByPointId(string pointId);
    public Task<PointDetail[]> ListPointDetails(string buildingDtId);
    public Task<DeviceDetail[]> ListDeviceDetails(string buildingDtId);

    /// <summary>
    /// Cross-resource search by name/business-id, optionally narrowed by resource type and/or building.
    /// Returns at most <paramref name="limit"/>+1 hits (the extra one lets callers detect more pages).
    /// </summary>
    public Task<ResourceSearchHit[]> SearchResources(string? q, string? type, string? buildingDtId, IReadOnlyList<string> tags, int limit, int offset);

    /// <summary>
    /// <see cref="SearchResources"/> narrowed by structured attributes (deviceType / pointType / unit /
    /// gatewayId, #454). Separate from it so the unfiltered search keeps its established contract.
    /// </summary>
    public Task<ResourceSearchHit[]> SearchResourcesFiltered(
        string? q, string? type, string? buildingDtId, IReadOnlyList<string> tags,
        ResourceAttributeFilter attrs, int limit, int offset, CancellationToken ct = default);

    /// <summary>
    /// The resources matching q / building / tags — of every type and with <b>no</b> attribute constraint —
    /// with the attributes facet counts are built from. The type and attribute filters are deliberately left
    /// to the caller: a facet group's counts must exclude that group's own selection, so one superset query
    /// serves every group. Returns at most <paramref name="rowCap"/>+1 rows (the extra one marks truncation).
    /// Unauthorized: the caller filters by read access before counting.
    /// </summary>
    public Task<ResourceFacetRow[]> ListFacetRows(
        string? q, string? buildingDtId, IReadOnlyList<string> tags, int rowCap, CancellationToken ct = default);

    /// <summary>
    /// Every (resource, customTag) pair whose tag is set to true and whose key starts with
    /// <paramref name="prefix"/> (case-insensitive; null/blank = no prefix filter). Unauthorized: the
    /// caller filters by read access before counting, so a count never reveals a resource it cannot read.
    /// </summary>
    public Task<ResourceTagUsage[]> ListTagUsage(string? prefix, CancellationToken ct = default);

    /// <summary>
    /// All points owned by a gateway (sbco:gatewayId), with native addressing / unit / writability /
    /// control schema / device grouping for the gateway point-list export (#224). Points with no native
    /// addressing still appear (null fields). Empty when the gateway owns no points.
    /// </summary>
    public Task<GatewayPointEntry[]> ListGatewayPointList(string gatewayId);

    /// <summary>
    /// Distinct gateway ids known to the twin (any <c>sbco:gatewayId</c> on a point), sorted. Used by
    /// the gateway admin surface (#323) to enumerate gateways. Empty when no gateway owns points.
    /// </summary>
    public Task<string[]> ListGatewayIds();

    /// <summary>
    /// Upsert or delete <c>sbco:identifiers</c> and <c>sbco:customTags</c> entries for a resource.
    /// A null value deletes the key; a non-null value upserts (delete existing + insert new).
    /// Either map may be null to leave that dimension unchanged.
    /// </summary>
    public Task UpdateResourceMetadataAsync(
        string dtId,
        Dictionary<string, string?>? identifiers,
        Dictionary<string, bool?>? customTags,
        CancellationToken ct);
}