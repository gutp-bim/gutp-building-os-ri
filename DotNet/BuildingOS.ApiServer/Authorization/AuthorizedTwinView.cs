using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOs.ApiServer.Authorization;

public sealed class AuthorizedTwinView(
    IDigitalTwinDatabase db,
    IAuthorizationService authService) : IAuthorizedTwinView
{
    // ── Building ──────────────────────────────────────────────────────────────

    public async Task<Building[]> ListBuildingsAsync(AuthorizationContext auth, CancellationToken ct)
    {
        var all = await db.ListBuildings();
        if (auth.IsAdmin) return all;
        var ids = await authService.GetAccessibleResourceIdsAsync(auth, "building", "read", ct).ConfigureAwait(false);
        return all.Where(b => ids.Contains(PermissionHelper.HashResourceId(b.DtId))).ToArray();
    }

    public async Task<TwinGetResult<Building>> GetBuildingAsync(AuthorizationContext auth, string buildingDtId, CancellationToken ct)
    {
        if (!auth.IsAdmin)
        {
            if (!await authService.CanAccessAsync(auth, "building", buildingDtId, "read", ct).ConfigureAwait(false))
                return new TwinGetResult<Building>.Forbidden();
        }
        var resource = await db.GetBuilding(buildingDtId);
        return resource is null ? new TwinGetResult<Building>.NotFound() : new TwinGetResult<Building>.Ok(resource);
    }

    // ── Floor ─────────────────────────────────────────────────────────────────

    public async Task<Floor[]> ListFloorsAsync(AuthorizationContext auth, string? buildingDtId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(buildingDtId))
            return auth.IsAdmin ? await db.ListFloors("") : [];

        var all = await db.ListFloors(buildingDtId);
        if (auth.IsAdmin) return all;
        if (await authService.CanAccessAsync(auth, "building", buildingDtId, "read", ct).ConfigureAwait(false)) return all;
        var ids = await authService.GetAccessibleResourceIdsAsync(auth, "floor", "read", ct).ConfigureAwait(false);
        return all.Where(f => ids.Contains(PermissionHelper.HashResourceId(f.DtId))).ToArray();
    }

    public async Task<TwinGetResult<Floor>> GetFloorAsync(AuthorizationContext auth, string floorDtId, CancellationToken ct)
    {
        if (!auth.IsAdmin)
        {
            if (!await authService.CanAccessAsync(auth, "floor", floorDtId, "read", ct).ConfigureAwait(false))
                return new TwinGetResult<Floor>.Forbidden();
        }
        var resource = await db.GetFloor(floorDtId);
        return resource is null ? new TwinGetResult<Floor>.NotFound() : new TwinGetResult<Floor>.Ok(resource);
    }

    // ── Space ─────────────────────────────────────────────────────────────────

    public async Task<Space[]> ListSpacesAsync(AuthorizationContext auth, string? floorDtId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(floorDtId))
            return auth.IsAdmin ? await db.ListSpaces("") : [];

        var all = await db.ListSpaces(floorDtId);
        if (auth.IsAdmin) return all;
        if (await authService.CanAccessAsync(auth, "floor", floorDtId, "read", ct).ConfigureAwait(false)) return all;
        var ids = await authService.GetAccessibleResourceIdsAsync(auth, "space", "read", ct).ConfigureAwait(false);
        return all.Where(s => ids.Contains(PermissionHelper.HashResourceId(s.DtId))).ToArray();
    }

    public async Task<TwinGetResult<Space>> GetSpaceAsync(AuthorizationContext auth, string spaceDtId, CancellationToken ct)
    {
        if (!auth.IsAdmin)
        {
            if (!await authService.CanAccessAsync(auth, "space", spaceDtId, "read", ct).ConfigureAwait(false))
                return new TwinGetResult<Space>.Forbidden();
        }
        var resource = await db.GetSpace(spaceDtId);
        return resource is null ? new TwinGetResult<Space>.NotFound() : new TwinGetResult<Space>.Ok(resource);
    }

    public async Task<TwinGetResult<Space[]>> ListAdjacentSpacesAsync(
        AuthorizationContext auth, string spaceDtId, CancellationToken ct)
    {
        // The controller percent-unescapes the route value, and the twin interpolates it into a
        // SPARQL IRI reference (<{spaceDtId}>) which has no escape mechanism — so a value that is
        // not a well-formed absolute IRI is rejected here, before the twin (or the authorization
        // service) is touched at all. NotFound rather than Forbidden or BadRequest: a value that
        // cannot be an IRI can never name a room, and answering 404 keeps a probe from telling a
        // rejected id apart from an id that is simply absent from the twin.
        if (!SparqlIriValidator.IsValidAbsoluteIri(spaceDtId))
            return new TwinGetResult<Space[]>.NotFound();

        if (!auth.IsAdmin)
        {
            if (!await authService.CanAccessAsync(auth, "space", spaceDtId, "read", ct).ConfigureAwait(false))
                return new TwinGetResult<Space[]>.Forbidden();
        }

        // "No such room" and "no neighbours" are the same empty adjacency list, so the subject's
        // existence is established separately.
        if (await db.GetSpace(spaceDtId).ConfigureAwait(false) is null)
            return new TwinGetResult<Space[]>.NotFound();

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
            if (await authService.CanAccessAsync(auth, "space", neighbour.DtId, "read", ct).ConfigureAwait(false))
                readable.Add(neighbour);
        }
        return new TwinGetResult<Space[]>.Ok(readable.ToArray());
    }

    // ── Device ────────────────────────────────────────────────────────────────

    public async Task<Device[]> ListDevicesAsync(AuthorizationContext auth, string? spaceDtId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(spaceDtId))
            return auth.IsAdmin ? await db.ListDevices("") : [];

        var all = await db.ListDevices(spaceDtId);
        if (auth.IsAdmin) return all;
        if (await authService.CanAccessAsync(auth, "space", spaceDtId, "read", ct).ConfigureAwait(false)) return all;
        var ids = await authService.GetAccessibleResourceIdsAsync(auth, "device", "read", ct).ConfigureAwait(false);
        return all.Where(d => ids.Contains(PermissionHelper.HashResourceId(d.DtId))).ToArray();
    }

    public async Task<TwinGetResult<Device>> GetDeviceAsync(AuthorizationContext auth, string deviceDtId, CancellationToken ct)
    {
        if (!auth.IsAdmin)
        {
            if (!await authService.CanAccessAsync(auth, "device", deviceDtId, "read", ct).ConfigureAwait(false))
                return new TwinGetResult<Device>.Forbidden();
        }
        var resource = await db.GetDevice(deviceDtId);
        return resource is null ? new TwinGetResult<Device>.NotFound() : new TwinGetResult<Device>.Ok(resource);
    }

    // ── Point ─────────────────────────────────────────────────────────────────

    public async Task<Point[]> ListPointsAsync(AuthorizationContext auth, string? deviceDtId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deviceDtId))
            return auth.IsAdmin ? await db.ListPoints("") : [];

        var all = await db.ListPoints(deviceDtId);
        if (auth.IsAdmin) return all;
        if (await authService.CanAccessAsync(auth, "device", deviceDtId, "read", ct).ConfigureAwait(false)) return all;
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

    public async Task<bool> CanWritePointAsync(AuthorizationContext auth, string pointId, CancellationToken ct)
    {
        // sbco:writable is a physical constraint — block even admins when explicitly false.
        var point = await db.GetPoint(pointId).ConfigureAwait(false);
        if (point is null || point.Writable == false) return false;

        return auth.IsAdmin || await authService.CanAccessAsync(auth, "point", pointId, "write", ct).ConfigureAwait(false);
    }

    public async Task<bool> CanWriteResourceAsync(
        AuthorizationContext auth, string resourceType, string resourceId, CancellationToken ct)
        => auth.IsAdmin
           || await authService.CanAccessAsync(auth, resourceType, resourceId, "write", ct).ConfigureAwait(false);

    // ── Search ────────────────────────────────────────────────────────────────

    public async Task<ResourceSearchHit[]> SearchAsync(
        AuthorizationContext auth, string? q, string? type, string? buildingDtId,
        IReadOnlyList<string> tags, int limit, int offset, CancellationToken ct)
    {
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

        var accessibleBuildings = await AccessibleAsync("building").ConfigureAwait(false);

        var filtered = new List<ResourceSearchHit>();
        foreach (var h in hits)
        {
            var ownIds = await AccessibleAsync(h.Type).ConfigureAwait(false);
            // Point authorizes by its business Id; everything else by DtId (matches the List* methods).
            var selfKey = PermissionHelper.HashResourceId(h.Type == "point" ? h.Id : h.DtId);
            var selfAllowed = ownIds.Contains(selfKey);

            // Building-ancestor grant: a user with read on the owning building sees its descendants.
            // h.BuildingDtId is only populated for a building-scoped search (?buildingId=...); in a
            // global search it is null, so ancestor grants surface via building-scoped search or the
            // tree browse (ListFloors/etc. already honor building grants), not the global query.
            var ancestorAllowed = !string.IsNullOrEmpty(h.BuildingDtId)
                && accessibleBuildings.Contains(PermissionHelper.HashResourceId(h.BuildingDtId));

            if (selfAllowed || ancestorAllowed) filtered.Add(h);
        }
        return filtered.ToArray();
    }
}
