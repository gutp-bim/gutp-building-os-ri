using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;

namespace BuildingOs.ApiServer.Authorization;

/// <summary>
/// Read/write authorization over the digital twin.
///
/// <para><b>dtId contract (#446).</b> Every <c>*DtId</c> parameter here is interpolated by the twin
/// into a SPARQL IRI reference, which has no escape mechanism. A value that is not a well-formed
/// absolute IRI is therefore rejected before the twin and before the authorization service are
/// consulted, and reported as "not found" — <c>NotFound</c> for a single-resource read, an empty
/// array for a collection — so a probe cannot tell a rejected id apart from one that is simply
/// absent from the twin. A blank scope id keeps its documented "no filter" meaning. Point ids are
/// not dtIds: they are matched as escaped string literals and are excluded from this rule.</para>
/// </summary>
public interface IAuthorizedTwinView
{
    Task<Building[]> ListBuildingsAsync(AuthorizationContext auth, CancellationToken ct);
    Task<TwinGetResult<Building>> GetBuildingAsync(AuthorizationContext auth, string buildingDtId, CancellationToken ct);

    Task<Floor[]> ListFloorsAsync(AuthorizationContext auth, string? buildingDtId, CancellationToken ct);
    Task<TwinGetResult<Floor>> GetFloorAsync(AuthorizationContext auth, string floorDtId, CancellationToken ct);

    Task<Space[]> ListSpacesAsync(AuthorizationContext auth, string? floorDtId, CancellationToken ct);
    Task<TwinGetResult<Space>> GetSpaceAsync(AuthorizationContext auth, string spaceDtId, CancellationToken ct);

    /// <summary>
    /// Rooms adjacent to <paramref name="spaceDtId"/> (#440), filtered by read authorization in two
    /// stages: the subject room must itself be readable (otherwise Forbidden — an unreadable room
    /// must not disclose how many neighbours it has), then each neighbour is kept only if it is
    /// readable on its own. NotFound when the subject room is not in the twin, which an empty
    /// adjacency list would otherwise be indistinguishable from. Admins bypass both stages.
    ///
    /// A <paramref name="spaceDtId"/> that is not a well-formed absolute IRI is also NotFound, and is
    /// rejected before either the twin or the authorization service is consulted — the id lands in a
    /// SPARQL IRI reference, which cannot be escaped. See <c>SparqlIriValidator</c>.
    /// </summary>
    Task<TwinGetResult<Space[]>> ListAdjacentSpacesAsync(
        AuthorizationContext auth, string spaceDtId, CancellationToken ct);

    Task<Device[]> ListDevicesAsync(AuthorizationContext auth, string? spaceDtId, CancellationToken ct);
    Task<TwinGetResult<Device>> GetDeviceAsync(AuthorizationContext auth, string deviceDtId, CancellationToken ct);

    Task<Point[]> ListPointsAsync(AuthorizationContext auth, string? deviceDtId, CancellationToken ct);
    Task<TwinGetResult<Point>> GetPointAsync(AuthorizationContext auth, string pointId, CancellationToken ct);

    /// <summary>
    /// Point + its parent Device (and floor/space) for a point business id. Used by control egress
    /// to resolve the point's gateway. Authorized as a point read.
    /// </summary>
    Task<TwinGetResult<PointDetail>> GetPointDetailAsync(AuthorizationContext auth, string pointId, CancellationToken ct);

    /// <summary>
    /// 建物 1 棟ぶんの Point 台帳（Point + Device/Floor/Space）を読み取り認可で絞って返す（#452）。
    /// データ健全性の判定は「台帳の全 Point」×「最終受信インデックス」の突き合わせなので、
    /// device 単位の <see cref="ListPointsAsync"/> ではなく建物単位の入口が要る。
    ///
    /// <para>絞り込み規則は <see cref="ListPointsAsync"/> と同じ考え方: admin は全件、建物の read 権が
    /// あれば全件、そうでなければ point（ビジネス ID）または所属 device（dtId）の直接付与ぶんだけ。
    /// IRI として使えない <paramref name="buildingDtId"/> は空配列（他の読み取りと同じ「無い」応答）。</para>
    /// </summary>
    Task<PointDetail[]> ListPointDetailsAsync(AuthorizationContext auth, string buildingDtId, CancellationToken ct);

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
