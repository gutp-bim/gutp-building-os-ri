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
    public Task<Device[]> ListDevices(string? spaceDtId);
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
}