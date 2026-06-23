using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using BuildingOs.ApiServer.GatewayProvisioning;
using BuildingOs.ApiServer.Middlewares;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// Gateway provisioning (#224): exports a gateway's point list (twin-authoritative) with native
/// addressing so the gateway can resolve protocol-native addressing → point_id locally and follow the
/// twin as it changes.
///
/// Machine auth, not user RBAC: the mTLS-terminating ingress injects the verified gateway id as a
/// trusted header (see <see cref="IGatewayIdentityResolver"/>); the endpoint requires that id to match
/// the path. An admin JWT (ops) bypasses the check for diagnostics. There is intentionally no
/// <c>[AuthorizeFilter]</c> — a gateway authenticates by mTLS, not a user JWT.
/// </summary>
[ApiController]
[Route("/gateways")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class GatewayProvisioningController(
    IDigitalTwinDatabase digitalTwinDatabase,
    IGatewayIdentityResolver gatewayIdentity,
    IGatewayPointListSnapshotStore snapshotStore) : ControllerBase
{
    /// <summary>
    /// 当該 gateway が所有する全 point（native addressing / unit / writable / control schema / device）を
    /// 返す。`If-None-Match` が現在の ETag と一致すれば 304。`?since={etag}` 指定時は差分
    /// （added/removed/changed）を返す（スナップショット未保持なら full にフォールバック）。
    /// </summary>
    [HttpGet("{gatewayId}/pointlist")]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPointList(string gatewayId, [FromQuery] string? since, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gatewayId)) return BadRequest("gatewayId is required");
        gatewayId = Uri.UnescapeDataString(gatewayId).Trim();

        // Auth binding: admin (ops) OR mTLS-derived caller gateway == path gateway.
        var auth = HttpContext.Items.TryGetValue(AuthorizationContextMiddleware.HttpContextKey, out var c)
            && c is AuthorizationContext a ? a : null;
        var isAdmin = auth?.IsAdmin == true;
        var caller = gatewayIdentity.ResolveGatewayId(Request.Headers);
        // Explicit 403 (not Forbid()) so the result does not depend on an authentication challenge
        // scheme — this route has no [AuthorizeFilter] (machine auth via the mTLS-derived header).
        if (!isAdmin && !string.Equals(caller, gatewayId, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status403Forbidden);

        var entries = await digitalTwinDatabase.ListGatewayPointList(gatewayId).ConfigureAwait(false);
        var etag = PointListEtag.Compute(entries);

        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "no-cache"; // always revalidate via ETag

        // Retain the current snapshot so a later ?since={etag} can be diffed against it.
        snapshotStore.Save(gatewayId, etag, entries);

        // ── Diff path (?since=) ────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(since))
        {
            if (since == etag) return StatusCode(StatusCodes.Status304NotModified);

            var previous = snapshotStore.Get(gatewayId, since);
            if (previous is null)
            {
                // Snapshot evicted / unknown base → full replace.
                return Ok(new GatewayPointListDiffResponse
                {
                    GatewayId = gatewayId, Revision = etag, Since = since, Full = true,
                    Points = entries.Select(GatewayPointDto.From).ToArray(),
                });
            }

            var diff = PointListDiffer.Diff(previous, entries);
            return Ok(new GatewayPointListDiffResponse
            {
                GatewayId = gatewayId, Revision = etag, Since = since, Full = false,
                Added = diff.Added.Select(GatewayPointDto.From).ToArray(),
                Removed = diff.Removed,
                Changed = diff.Changed.Select(GatewayPointDto.From).ToArray(),
            });
        }

        // ── Full path ──────────────────────────────────────────────────────────
        var ifNoneMatch = Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etag)
            return StatusCode(StatusCodes.Status304NotModified);

        return Ok(new GatewayPointListResponse
        {
            GatewayId = gatewayId,
            Revision = etag,
            GeneratedAt = DateTime.UtcNow,
            Points = entries.Select(GatewayPointDto.From).ToArray(),
        });
    }
}
