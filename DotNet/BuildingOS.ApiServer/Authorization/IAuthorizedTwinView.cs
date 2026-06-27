using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;

namespace BuildingOs.ApiServer.Authorization;

public interface IAuthorizedTwinView
{
    Task<Building[]> ListBuildingsAsync(AuthorizationContext auth, CancellationToken ct);
    Task<TwinGetResult<Building>> GetBuildingAsync(AuthorizationContext auth, string buildingDtId, CancellationToken ct);

    Task<Floor[]> ListFloorsAsync(AuthorizationContext auth, string? buildingDtId, CancellationToken ct);
    Task<TwinGetResult<Floor>> GetFloorAsync(AuthorizationContext auth, string floorDtId, CancellationToken ct);

    Task<Space[]> ListSpacesAsync(AuthorizationContext auth, string? floorDtId, CancellationToken ct);
    Task<TwinGetResult<Space>> GetSpaceAsync(AuthorizationContext auth, string spaceDtId, CancellationToken ct);

    Task<Device[]> ListDevicesAsync(AuthorizationContext auth, string? spaceDtId, CancellationToken ct);
    Task<TwinGetResult<Device>> GetDeviceAsync(AuthorizationContext auth, string deviceDtId, CancellationToken ct);

    Task<Point[]> ListPointsAsync(AuthorizationContext auth, string? deviceDtId, CancellationToken ct);
    Task<TwinGetResult<Point>> GetPointAsync(AuthorizationContext auth, string pointId, CancellationToken ct);

    /// <summary>
    /// Point + its parent Device (and floor/space) for a point business id. Used by control egress
    /// to resolve the point's gateway. Authorized as a point read.
    /// </summary>
    Task<TwinGetResult<PointDetail>> GetPointDetailAsync(AuthorizationContext auth, string pointId, CancellationToken ct);

    Task<bool> CanWritePointAsync(AuthorizationContext auth, string pointId, CancellationToken ct);

    /// <summary>
    /// Cross-resource search filtered by read authorization. Admins see all hits; other users see a
    /// hit when its own id is readable, or when its owning building is readable (ancestor grant).
    /// </summary>
    Task<ResourceSearchHit[]> SearchAsync(
        AuthorizationContext auth, string? q, string? type, string? buildingDtId,
        IReadOnlyList<string> tags, int limit, int offset, CancellationToken ct);

    /// <summary>
    /// Returns true when the caller has write access to any resource type.
    /// Admins always have write access.
    /// </summary>
    Task<bool> CanWriteResourceAsync(
        AuthorizationContext auth, string resourceType, string resourceId, CancellationToken ct);
}
