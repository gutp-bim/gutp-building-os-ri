using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;

namespace BuildingOs.ApiServer.Authorization;

/// <summary>
/// Authorizes a twin node the caller addressed by dtId (#504). Authorization lives in the business-id
/// space (sbco:id): Group items are stored by business id, and CanAccessAsync's ancestor chain
/// (OxiGraphHierarchyResolver) matches sbco:id literals and returns business ids. So the node is
/// loaded and asked about by its business id, never by the dtId it was requested with.
/// <para>
/// Migration: a grant recorded against the dtId keeps working — the dtId is asked about only when
/// the business-id check fails (a direct grant; no ancestor or group resolution exists in the dtId
/// space). Shared by <see cref="AuthorizedTwinView"/> and the building-scoped detail controllers so
/// every read of the same node gives the same answer.
/// </para>
/// </summary>
public static class NodeAuthorization
{
    /// <summary>The business id of the building/floor/space/device at <paramref name="dtId"/>, or null when absent.</summary>
    public static async Task<string?> BusinessIdAsync(IDigitalTwinDatabase db, string resourceType, string dtId)
        => resourceType switch
        {
            "building" => (await db.GetBuilding(dtId).ConfigureAwait(false))?.Id,
            "floor" => (await db.GetFloor(dtId).ConfigureAwait(false))?.Id,
            "space" => (await db.GetSpace(dtId).ConfigureAwait(false))?.Id,
            "device" => (await db.GetDevice(dtId).ConfigureAwait(false))?.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, "not a dtId-addressed node type"),
        };

    /// <summary>
    /// <paramref name="action"/> on the node addressed by <paramref name="dtId"/>, whose business id is
    /// <paramref name="businessId"/> (null when the node is not in the twin: then only the legacy check applies).
    /// </summary>
    public static async Task<bool> CanAccessAsync(
        IAuthorizationService authService, AuthorizationContext auth,
        string resourceType, string dtId, string? businessId, string action, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(businessId)
            && await authService.CanAccessAsync(auth, resourceType, businessId, action, ct).ConfigureAwait(false))
            return true;
        return await authService.CanAccessAsync(auth, resourceType, dtId, action, ct).ConfigureAwait(false);
    }

    /// <summary>Loads the node's business id, then <see cref="CanAccessAsync(IAuthorizationService, AuthorizationContext, string, string, string?, string, CancellationToken)"/>.</summary>
    public static async Task<bool> CanAccessByDtIdAsync(
        IDigitalTwinDatabase db, IAuthorizationService authService, AuthorizationContext auth,
        string resourceType, string dtId, string action, CancellationToken ct)
        => await CanAccessAsync(authService, auth, resourceType, dtId,
            await BusinessIdAsync(db, resourceType, dtId).ConfigureAwait(false), action, ct).ConfigureAwait(false);
}
