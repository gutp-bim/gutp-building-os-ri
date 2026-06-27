using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// Phase 0 placeholder: OxiGraph (SPARQL) implementation will replace this in Phase 3.
/// </summary>
public class OssDigitalTwinDatabase(ILogger<OssDigitalTwinDatabase> logger) : IDigitalTwinDatabase
{
    public Task<Building[]> ListBuildings()
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.ListBuildings — no-op placeholder");
        return Task.FromResult(Array.Empty<Building>());
    }

    public Task<Building?> GetBuilding(string dtId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.GetBuilding({DtId}) — no-op placeholder", dtId);
        return Task.FromResult<Building?>(null);
    }

    public Task<Floor[]> ListFloors(string? buildingDtId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.ListFloors — no-op placeholder");
        return Task.FromResult(Array.Empty<Floor>());
    }

    public Task<Floor?> GetFloor(string dtId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.GetFloor({DtId}) — no-op placeholder", dtId);
        return Task.FromResult<Floor?>(null);
    }

    public Task<Space[]> ListSpaces(string? floorDtId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.ListSpaces — no-op placeholder");
        return Task.FromResult(Array.Empty<Space>());
    }

    public Task<Space?> GetSpace(string dtId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.GetSpace({DtId}) — no-op placeholder", dtId);
        return Task.FromResult<Space?>(null);
    }

    public Task<Device[]> ListDevices(string? spaceDtId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.ListDevices — no-op placeholder");
        return Task.FromResult(Array.Empty<Device>());
    }

    public Task<Device?> GetDevice(string dtId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.GetDevice({DtId}) — no-op placeholder", dtId);
        return Task.FromResult<Device?>(null);
    }

    public Task<Point[]> ListPoints(string? deviceDtId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.ListPoints — no-op placeholder");
        return Task.FromResult(Array.Empty<Point>());
    }

    public Task<Point?> GetPoint(string pointId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.GetPoint({PointId}) — no-op placeholder", pointId);
        return Task.FromResult<Point?>(null);
    }

    public Task<PointDetail?> GetPointDetailByPointId(string pointId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.GetPointDetailByPointId — no-op placeholder");
        return Task.FromResult<PointDetail?>(null);
    }

    public Task<PointDetail[]> ListPointDetails(string buildingDtId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.ListPointDetails — no-op placeholder");
        return Task.FromResult(Array.Empty<PointDetail>());
    }

    public Task<DeviceDetail[]> ListDeviceDetails(string buildingDtId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.ListDeviceDetails — no-op placeholder");
        return Task.FromResult(Array.Empty<DeviceDetail>());
    }

    public Task<ResourceSearchHit[]> SearchResources(string? q, string? type, string? buildingDtId, IReadOnlyList<string> tags, int limit, int offset)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.SearchResources — no-op placeholder");
        return Task.FromResult(Array.Empty<ResourceSearchHit>());
    }

    public Task<GatewayPointEntry[]> ListGatewayPointList(string gatewayId)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.ListGatewayPointList — no-op placeholder");
        return Task.FromResult(Array.Empty<GatewayPointEntry>());
    }

    public Task<string[]> ListGatewayIds()
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.ListGatewayIds — no-op placeholder");
        return Task.FromResult(Array.Empty<string>());
    }

    public Task UpdateResourceMetadataAsync(
        string dtId,
        Dictionary<string, string?>? identifiers,
        Dictionary<string, bool?>? customTags,
        CancellationToken ct)
    {
        logger.LogWarning("[OSS] OssDigitalTwinDatabase.UpdateResourceMetadataAsync — no-op placeholder");
        return Task.CompletedTask;
    }
}
