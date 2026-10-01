using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOs.ApiServer.Authorization;

/// <summary>
/// 読み取り認可を通した twin ビュー。controller は必ずこれ越しに台帳を読む。
/// </summary>
/// <param name="db">twin（OxiGraph）の読み取り。</param>
/// <param name="authService">リソース単位の認可判定。</param>
/// <param name="inventory">
/// 建物ごとの Point 台帳の短 TTL キャッシュ（#452）。**認可前**の twin データだけを保持し、
/// 絞り込みは <see cref="ListPointDetailsAsync"/> がキャッシュヒットでも毎回やり直す。
/// null なら毎回 twin を読む（データ健全性以外の経路は従来どおり）。
/// </param>
public sealed class AuthorizedTwinView(
    IDigitalTwinDatabase db,
    IAuthorizationService authService,
    PointDetailInventoryCache? inventory = null) : IAuthorizedTwinView
{
    // ── dtId guard (#446) ─────────────────────────────────────────────────────
    //
    // Every dtId below is interpolated by the twin into a SPARQL IRI reference (<{dtId}>), which has
    // no escape mechanism — the controllers percent-unescape the route value first, so a hostile
    // "%3E" arrives here as a raw ">" that would end the token and let the rest be read as query
    // syntax. So a value that is not a well-formed absolute IRI is rejected here, before the twin
    // *and* before the authorization service: asking the ACL first would answer Forbidden for one
    // malformed id and NotFound for another, which is the very oracle the uniform "not found" denies.
    // #444 established this on ListAdjacentSpacesAsync; the rest of the read paths follow it.
    //
    // Point ids are deliberately not guarded: they are matched as SPARQL string literals
    // (FILTER(?ptId = "…"), escaped by EscapeStringLiteral) and a business id like "PT001" is not an
    // IRI at all. Nor is CanWriteResourceAsync, whose resourceId is a dtId for four types and a
    // point business id for the fifth — the metadata write path is guarded in the twin
    // implementation instead (see OxiGraphDigitalTwinDatabase).
    private static bool IsUsableDtId(string? dtId) => SparqlIriValidator.IsValidAbsoluteIri(dtId);

    // A blank scope id is the documented "no filter" input of the list reads, not a malformed IRI —
    // it never reaches an interpolation, so only a non-blank value is validated.
    private static bool IsUnusableScopeId(string? scopeDtId)
        => !string.IsNullOrEmpty(scopeDtId) && !IsUsableDtId(scopeDtId);

    // ── Id space (#504) ───────────────────────────────────────────────────────
    //
    // Nodes are authorized by business id (sbco:id), not by the dtId they are addressed by — see
    // NodeAuthorization. A grant recorded against a dtId keeps matching during migration.

    /// <summary>Whether a hashed id-set grants the node, by business id or (legacy) dtId.</summary>
    private static bool Grants(IReadOnlyCollection<string> hashedIds, string businessId, string dtId)
        => hashedIds.Contains(PermissionHelper.HashResourceId(businessId))
           || hashedIds.Contains(PermissionHelper.HashResourceId(dtId));

    private async Task<HashSet<string>> AccessibleIdSetAsync(
        AuthorizationContext auth, string resourceType, CancellationToken ct)
        => (await authService.GetAccessibleResourceIdsAsync(auth, resourceType, "read", ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

    private Task<bool> CanReadNodeAsync(
        AuthorizationContext auth, string resourceType, string dtId, string? businessId, CancellationToken ct)
        => NodeAuthorization.CanAccessAsync(authService, auth, resourceType, dtId, businessId, "read", ct);

    /// <summary>
    /// Get-by-dtId shape shared by the four node types: the node is loaded first (its business id is
    /// what authorizes it), and a non-admin gets Forbidden for an absent node exactly as for an
    /// unreadable one, so the order does not become an existence oracle.
    /// </summary>
    private async Task<TwinGetResult<T>> GetNodeAsync<T>(
        AuthorizationContext auth, string resourceType, string dtId, Func<Task<T?>> load, Func<T, string> businessId,
        CancellationToken ct) where T : class
    {
        if (!IsUsableDtId(dtId)) return new TwinGetResult<T>.NotFound();
        var resource = await load().ConfigureAwait(false);
        if (!auth.IsAdmin
            && (resource is null
                || !await CanReadNodeAsync(auth, resourceType, dtId, businessId(resource), ct).ConfigureAwait(false)))
            return new TwinGetResult<T>.Forbidden();
        return resource is null ? new TwinGetResult<T>.NotFound() : new TwinGetResult<T>.Ok(resource);
    }

    // ── Building ──────────────────────────────────────────────────────────────

    public async Task<Building[]> ListBuildingsAsync(AuthorizationContext auth, CancellationToken ct)
    {
        var all = await db.ListBuildings();
        if (auth.IsAdmin) return all;
        var ids = await authService.GetAccessibleResourceIdsAsync(auth, "building", "read", ct).ConfigureAwait(false);
        return all.Where(b => Grants(ids, b.Id, b.DtId)).ToArray();
    }

    public Task<TwinGetResult<Building>> GetBuildingAsync(AuthorizationContext auth, string buildingDtId, CancellationToken ct)
        => GetNodeAsync(auth, "building", buildingDtId, () => db.GetBuilding(buildingDtId), b => b.Id, ct);

    // ── Floor ─────────────────────────────────────────────────────────────────

    public async Task<Floor[]> ListFloorsAsync(AuthorizationContext auth, string? buildingDtId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(buildingDtId))
            return auth.IsAdmin ? await db.ListFloors("") : [];
        if (IsUnusableScopeId(buildingDtId)) return [];

        var all = await db.ListFloors(buildingDtId);
        if (auth.IsAdmin) return all;
        var parent = await db.GetBuilding(buildingDtId).ConfigureAwait(false);
        if (await CanReadNodeAsync(auth, "building", buildingDtId, parent?.Id, ct).ConfigureAwait(false)) return all;
        var ids = await authService.GetAccessibleResourceIdsAsync(auth, "floor", "read", ct).ConfigureAwait(false);
        return all.Where(f => Grants(ids, f.Id, f.DtId)).ToArray();
    }

    public Task<TwinGetResult<Floor>> GetFloorAsync(AuthorizationContext auth, string floorDtId, CancellationToken ct)
        => GetNodeAsync(auth, "floor", floorDtId, () => db.GetFloor(floorDtId), f => f.Id, ct);

    // ── Space ─────────────────────────────────────────────────────────────────

    public async Task<Space[]> ListSpacesAsync(AuthorizationContext auth, string? floorDtId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(floorDtId))
            return auth.IsAdmin ? await db.ListSpaces("") : [];
        if (IsUnusableScopeId(floorDtId)) return [];

        var all = await db.ListSpaces(floorDtId);
        if (auth.IsAdmin) return all;
        var parent = await db.GetFloor(floorDtId).ConfigureAwait(false);
        if (await CanReadNodeAsync(auth, "floor", floorDtId, parent?.Id, ct).ConfigureAwait(false)) return all;
        var ids = await authService.GetAccessibleResourceIdsAsync(auth, "space", "read", ct).ConfigureAwait(false);
        return all.Where(s => Grants(ids, s.Id, s.DtId)).ToArray();
    }

    public Task<TwinGetResult<Space>> GetSpaceAsync(AuthorizationContext auth, string spaceDtId, CancellationToken ct)
        => GetNodeAsync(auth, "space", spaceDtId, () => db.GetSpace(spaceDtId), s => s.Id, ct);

    public async Task<TwinGetResult<Space[]>> ListAdjacentSpacesAsync(
        AuthorizationContext auth, string spaceDtId, CancellationToken ct)
    {
        // NotFound rather than Forbidden or BadRequest: a value that cannot be an IRI can never name
        // a room, and answering 404 keeps a probe from telling a rejected id apart from an id that
        // is simply absent from the twin. See the dtId guard note at the top of this class.
        if (!IsUsableDtId(spaceDtId)) return new TwinGetResult<Space[]>.NotFound();

        // "No such room" and "no neighbours" are the same empty adjacency list, so the subject's
        // existence is established separately — and first, since its business id is what authorizes
        // it (#504). As in GetNodeAsync, a non-admin gets Forbidden for an absent room too.
        var subject = await db.GetSpace(spaceDtId).ConfigureAwait(false);
        if (!auth.IsAdmin
            && (subject is null
                || !await CanReadNodeAsync(auth, "space", spaceDtId, subject.Id, ct).ConfigureAwait(false)))
            return new TwinGetResult<Space[]>.Forbidden();
        if (subject is null) return new TwinGetResult<Space[]>.NotFound();

        var neighbours = await db.ListAdjacentSpaces(spaceDtId).ConfigureAwait(false);
        if (auth.IsAdmin) return new TwinGetResult<Space[]>.Ok(neighbours);

        // One CanAccessAsync per neighbour rather than a hash match against
        // GetAccessibleResourceIdsAsync("space"): CanAccessAsync already resolves the ancestor chain
        // (a building/floor grant covers its rooms) and group permissions, so the id-set shortcut
        // would hide neighbours the caller can read through GET /spaces/{id}. Adjacency degree is a
        // handful of rooms, so the extra calls are not worth optimizing away.
        var readable = new List<Space>();
        foreach (var neighbour in neighbours)
        {
            if (await CanReadNodeAsync(auth, "space", neighbour.DtId, neighbour.Id, ct).ConfigureAwait(false))
                readable.Add(neighbour);
        }
        return new TwinGetResult<Space[]>.Ok(readable.ToArray());
    }

    // ── Device ────────────────────────────────────────────────────────────────

    public async Task<Device[]> ListDevicesAsync(AuthorizationContext auth, string? spaceDtId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(spaceDtId))
            return auth.IsAdmin ? await db.ListDevices("") : [];
        if (IsUnusableScopeId(spaceDtId)) return [];

        var all = await db.ListDevices(spaceDtId);
        if (auth.IsAdmin) return all;
        var parent = await db.GetSpace(spaceDtId).ConfigureAwait(false);
        if (await CanReadNodeAsync(auth, "space", spaceDtId, parent?.Id, ct).ConfigureAwait(false)) return all;
        var ids = await authService.GetAccessibleResourceIdsAsync(auth, "device", "read", ct).ConfigureAwait(false);
        return all.Where(d => Grants(ids, d.Id, d.DtId)).ToArray();
    }

    public Task<TwinGetResult<Device>> GetDeviceAsync(AuthorizationContext auth, string deviceDtId, CancellationToken ct)
        => GetNodeAsync(auth, "device", deviceDtId, () => db.GetDevice(deviceDtId), d => d.Id, ct);

    // ── Point ─────────────────────────────────────────────────────────────────

    public async Task<Point[]> ListPointsAsync(AuthorizationContext auth, string? deviceDtId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deviceDtId))
            return auth.IsAdmin ? await db.ListPoints("") : [];
        // The device scope reaches SPARQL as VALUES ?dev { <deviceDtId> } (BuildPointSelect) rather
        // than as a triple pattern, which is why it reads as an exception at a glance — it is not:
        // that is an IRI reference like every other dtId here.
        if (IsUnusableScopeId(deviceDtId)) return [];

        var all = await db.ListPoints(deviceDtId);
        if (auth.IsAdmin) return all;
        var parent = await db.GetDevice(deviceDtId).ConfigureAwait(false);
        if (await CanReadNodeAsync(auth, "device", deviceDtId, parent?.Id, ct).ConfigureAwait(false)) return all;
        // Point は DtId ではなくビジネス ID（Point.Id）で権限照合する
        var ids = await authService.GetAccessibleResourceIdsAsync(auth, "point", "read", ct).ConfigureAwait(false);
        return all.Where(p => ids.Contains(PermissionHelper.HashResourceId(p.Id))).ToArray();
    }

    public async Task<TwinGetResult<Point>> GetPointAsync(AuthorizationContext auth, string pointId, CancellationToken ct)
    {
        if (!auth.IsAdmin)
        {
            if (!await authService.CanAccessAsync(auth, "point", pointId, "read", ct).ConfigureAwait(false))
                return new TwinGetResult<Point>.Forbidden();
        }
        var resource = await db.GetPoint(pointId);
        return resource is null ? new TwinGetResult<Point>.NotFound() : new TwinGetResult<Point>.Ok(resource);
    }

    public async Task<TwinGetResult<PointDetail>> GetPointDetailAsync(AuthorizationContext auth, string pointId, CancellationToken ct)
    {
        if (!auth.IsAdmin)
        {
            if (!await authService.CanAccessAsync(auth, "point", pointId, "read", ct).ConfigureAwait(false))
                return new TwinGetResult<PointDetail>.Forbidden();
        }
        var resource = await db.GetPointDetailByPointId(pointId);
        return resource is null ? new TwinGetResult<PointDetail>.NotFound() : new TwinGetResult<PointDetail>.Ok(resource);
    }

    public async Task<PointDetail[]> ListPointDetailsAsync(
        AuthorizationContext auth, string buildingDtId, CancellationToken ct)
    {
        if (!IsUsableDtId(buildingDtId)) return [];

        // 認可の判定を**台帳を読む前に**済ませる。buildingDtId は呼び出し元がクエリで自由に指定できるので、
        // 先に読み込んでから絞ると「1 件も読めない利用者」でも建物全件の SPARQL と認可前キャッシュの
        // 充填を誘発できてしまう（データは漏れないが、存在しない ID を並べるだけで台帳キャッシュを
        // 太らせられる）。読める見込みがゼロなら台帳に触れずに空を返す。
        HashSet<string> pointIds = [];
        HashSet<string> deviceIds = [];
        HashSet<string> spaceIds = [];
        HashSet<string> floorIds = [];
        // Only the building node (one small read) is loaded ahead of the check, for its business id —
        // the ledger itself is still not touched until the caller is known to read something.
        var readsWholeBuilding = auth.IsAdmin
            || await CanReadNodeAsync(auth, "building", buildingDtId,
                (await db.GetBuilding(buildingDtId).ConfigureAwait(false))?.Id, ct).ConfigureAwait(false);
        if (!readsWholeBuilding)
        {
            // 建物の権限が無ければ、付与された point / device / space / floor の範囲だけ見せる
            // （ListPointsAsync の「device の read 権があればその配下の Point は読める」を建物スコープに
            // 写したもの）。space / floor（#518）は、ツリーで部屋・フロアから辿れる範囲と台帳を揃えるため。
            // どの集合も Group 経由の付与を含む（GetAccessibleResourceIdsAsync が展開する）。
            // 祖先の判定を Device ごとの CanAccessAsync（祖先 SPARQL + Group 照会）でやると数千回に
            // なるので、ハッシュ集合と各行の space / floor の業務 ID を照合する。
            // HashSet に移す。台帳は建物 1 棟で数千 Point になり得るので、IReadOnlyList の Contains
            // （線形探索）のままだと絞り込みが O(Point 数 × 許可 ID 数) になる（#460 レビュー）。
            pointIds = await AccessibleIdSetAsync(auth, "point", ct).ConfigureAwait(false);
            deviceIds = await AccessibleIdSetAsync(auth, "device", ct).ConfigureAwait(false);
            spaceIds = await AccessibleIdSetAsync(auth, "space", ct).ConfigureAwait(false);
            floorIds = await AccessibleIdSetAsync(auth, "floor", ct).ConfigureAwait(false);
            if (pointIds.Count == 0 && deviceIds.Count == 0 && spaceIds.Count == 0 && floorIds.Count == 0)
                return [];
        }

        // キャッシュに載るのは**認可前**の twin データ。絞り込みは毎リクエストこの下で適用する。
        var all = inventory is null
            ? await db.ListPointDetails(buildingDtId).ConfigureAwait(false)
            : await inventory.GetAsync(buildingDtId, _ => db.ListPointDetails(buildingDtId), ct).ConfigureAwait(false);

        if (readsWholeBuilding) return all;
        return all.Where(d =>
                pointIds.Contains(PermissionHelper.HashResourceId(d.Point.Id))
                || (d.Device is not null && Grants(deviceIds, d.Device.Id, d.Device.DtId))
                || (d.Space is not null && Grants(spaceIds, d.Space.Id, d.Space.DtId))
                || (d.Floor is not null && Grants(floorIds, d.Floor.Id, d.Floor.DtId)))
            .ToArray();
    }

    public async Task<bool> CanWritePointAsync(AuthorizationContext auth, string pointId, CancellationToken ct)
    {
        // sbco:writable is a physical constraint — block even admins when explicitly false.
        var point = await db.GetPoint(pointId).ConfigureAwait(false);
        if (point is null || point.Writable == false) return false;

        return auth.IsAdmin || await authService.CanAccessAsync(auth, "point", pointId, "write", ct).ConfigureAwait(false);
    }

    public async Task<bool> CanWriteResourceAsync(
        AuthorizationContext auth, string resourceType, string resourceId, CancellationToken ct)
    {
        if (auth.IsAdmin) return true;
        // A point is addressed by its business id already; the other four types by dtId, so they are
        // authorized by the business id the node carries, like the reads (#504).
        return resourceType == "point"
            ? await authService.CanAccessAsync(auth, "point", resourceId, "write", ct).ConfigureAwait(false)
            : await NodeAuthorization.CanAccessByDtIdAsync(db, authService, auth, resourceType, resourceId, "write", ct)
                .ConfigureAwait(false);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    public async Task<ResourceSearchHit[]> SearchAsync(
        AuthorizationContext auth, string? q, string? type, string? buildingDtId,
        IReadOnlyList<string> tags, int limit, int offset, CancellationToken ct)
    {
        // ?buildingDtId= is user input that ResourceSearchQueryBuilder interpolates into an IRI
        // reference in every UNION branch (FILTER(?dt = <…>), <…> sbco:hasPart ?dt, …). The builder
        // is pure and has no way to report a rejection, so the scope is validated here; q and the
        // tags are string literals and stay on EscapeStringLiteral. An unusable scope names no
        // building, so the search finds nothing — the same answer an unknown building gets.
        if (IsUnusableScopeId(buildingDtId)) return [];

        var hits = await db.SearchResources(q, type, buildingDtId, tags, limit, offset).ConfigureAwait(false);
        if (auth.IsAdmin) return hits;

        // Resolve accessible-id sets lazily, one ACL call per distinct resource type encountered.
        var accessibleByType = new Dictionary<string, IReadOnlyList<string>>();
        async Task<IReadOnlyList<string>> AccessibleAsync(string resourceType)
        {
            if (!accessibleByType.TryGetValue(resourceType, out var ids))
            {
                ids = await authService.GetAccessibleResourceIdsAsync(auth, resourceType, "read", ct).ConfigureAwait(false);
                accessibleByType[resourceType] = ids;
            }
            return ids;
        }

        // Building-ancestor grant: a user who reads the scoped building sees its descendants. Only a
        // building-scoped search (?buildingDtId=…) has one building to ask about, so it is authorized
        // once, by business id (#504); in a global search ancestor grants surface via a
        // building-scoped search or the tree browse, not the global query.
        if (!string.IsNullOrEmpty(buildingDtId)
            && await CanReadNodeAsync(auth, "building", buildingDtId,
                (await db.GetBuilding(buildingDtId).ConfigureAwait(false))?.Id, ct).ConfigureAwait(false))
            return hits;

        var filtered = new List<ResourceSearchHit>();
        foreach (var h in hits)
        {
            var ownIds = await AccessibleAsync(h.Type).ConfigureAwait(false);
            // Every type authorizes by its business id (#504). A legacy dtId grant still matches for
            // the node types; points were only ever granted by business id, so none exists to honour.
            var selfAllowed = h.Type == "point"
                ? ownIds.Contains(PermissionHelper.HashResourceId(h.Id))
                : Grants(ownIds, h.Id, h.DtId);
            if (selfAllowed) filtered.Add(h);
        }
        return filtered.ToArray();
    }
}
