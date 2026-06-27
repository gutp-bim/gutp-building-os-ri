using BuildingOS.Shared.Infrastructure;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;
using Entities = BuildingOS.Shared;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// GET  /&lt;resourceType&gt;/{id}/metadata — read identifiers and customTags.
/// PATCH /&lt;resourceType&gt;/{id}/metadata — upsert/delete keys. Null value = delete.
/// </summary>
[ApiController]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[AuthorizeFilter]
public class ResourceMetadataController(
    IAuthorizedTwinView twinView,
    IDigitalTwinDatabase db) : ControllerBase
{
    // ── GET ──────────────────────────────────────────────────────────────────

    [HttpGet("/buildings/{buildingDtId}/metadata")]
    [ProducesResponseType(typeof(ResourceMetadataResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResourceMetadataResponse>> GetBuilding(string buildingDtId, CancellationToken ct)
    {
        var r = await twinView.GetBuildingAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(buildingDtId), ct).ConfigureAwait(false);
        return r switch
        {
            TwinGetResult<Entities.Building>.Ok ok => Ok(MetadataOf(ok.Resource.Identifiers, ok.Resource.CustomTags)),
            TwinGetResult<Entities.Building>.Forbidden => Forbid(),
            _ => NotFound(),
        };
    }

    [HttpGet("/floors/{floorDtId}/metadata")]
    [ProducesResponseType(typeof(ResourceMetadataResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResourceMetadataResponse>> GetFloor(string floorDtId, CancellationToken ct)
    {
        var r = await twinView.GetFloorAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(floorDtId), ct).ConfigureAwait(false);
        return r switch
        {
            TwinGetResult<Entities.Floor>.Ok ok => Ok(MetadataOf(ok.Resource.Identifiers, ok.Resource.CustomTags)),
            TwinGetResult<Entities.Floor>.Forbidden => Forbid(),
            _ => NotFound(),
        };
    }

    [HttpGet("/spaces/{spaceDtId}/metadata")]
    [ProducesResponseType(typeof(ResourceMetadataResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResourceMetadataResponse>> GetSpace(string spaceDtId, CancellationToken ct)
    {
        var r = await twinView.GetSpaceAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(spaceDtId), ct).ConfigureAwait(false);
        return r switch
        {
            TwinGetResult<Entities.Space>.Ok ok => Ok(MetadataOf(ok.Resource.Identifiers, ok.Resource.CustomTags)),
            TwinGetResult<Entities.Space>.Forbidden => Forbid(),
            _ => NotFound(),
        };
    }

    [HttpGet("/devices/{deviceDtId}/metadata")]
    [ProducesResponseType(typeof(ResourceMetadataResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResourceMetadataResponse>> GetDevice(string deviceDtId, CancellationToken ct)
    {
        var r = await twinView.GetDeviceAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(deviceDtId), ct).ConfigureAwait(false);
        return r switch
        {
            TwinGetResult<Entities.Device>.Ok ok => Ok(MetadataOf(ok.Resource.Identifiers, ok.Resource.CustomTags)),
            TwinGetResult<Entities.Device>.Forbidden => Forbid(),
            _ => NotFound(),
        };
    }

    [HttpGet("/points/{pointId}/metadata")]
    [ProducesResponseType(typeof(ResourceMetadataResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResourceMetadataResponse>> GetPointMetadata(string pointId, CancellationToken ct)
    {
        var r = await twinView.GetPointAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(pointId), ct).ConfigureAwait(false);
        return r switch
        {
            TwinGetResult<Entities.Point>.Ok ok => Ok(MetadataOf(ok.Resource.Identifiers, ok.Resource.CustomTags)),
            TwinGetResult<Entities.Point>.Forbidden => Forbid(),
            _ => NotFound(),
        };
    }

    // ── PATCH ─────────────────────────────────────────────────────────────────

    [HttpPatch("/buildings/{buildingDtId}/metadata")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> PatchBuilding(string buildingDtId, [FromBody] ResourceMetadataPatchRequest req, CancellationToken ct)
        => PatchAsync("building", Uri.UnescapeDataString(buildingDtId), req, ct);

    [HttpPatch("/floors/{floorDtId}/metadata")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> PatchFloor(string floorDtId, [FromBody] ResourceMetadataPatchRequest req, CancellationToken ct)
        => PatchAsync("floor", Uri.UnescapeDataString(floorDtId), req, ct);

    [HttpPatch("/spaces/{spaceDtId}/metadata")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> PatchSpace(string spaceDtId, [FromBody] ResourceMetadataPatchRequest req, CancellationToken ct)
        => PatchAsync("space", Uri.UnescapeDataString(spaceDtId), req, ct);

    [HttpPatch("/devices/{deviceDtId}/metadata")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> PatchDevice(string deviceDtId, [FromBody] ResourceMetadataPatchRequest req, CancellationToken ct)
        => PatchAsync("device", Uri.UnescapeDataString(deviceDtId), req, ct);

    /// <summary>
    /// Points use the logical pointId in the URL (matching the existing PointController convention).
    /// The logical pointId is resolved to the twin DtId (RDF IRI) before writing SPARQL,
    /// so triples land under the correct subject and are readable via the twin's standard IRI.
    /// </summary>
    [HttpPatch("/points/{pointId}/metadata")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> PatchPoint(string pointId, [FromBody] ResourceMetadataPatchRequest req, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthorizationContext();
        var id = Uri.UnescapeDataString(pointId);

        if (!await twinView.CanWriteResourceAsync(auth, "point", id, ct).ConfigureAwait(false))
            return Forbid();

        // Resolve the twin's DtId (RDF IRI) — the logical pointId is the URL convention for points
        // but the SPARQL subject must be the IRI (e.g., urn:dtid:pt1).
        var point = await db.GetPoint(id).ConfigureAwait(false);
        if (point == null) return NotFound();

        await db.UpdateResourceMetadataAsync(point.DtId, req.Identifiers, req.CustomTags, ct).ConfigureAwait(false);
        return NoContent();
    }

    private async Task<IActionResult> PatchAsync(
        string resourceType, string resourceId,
        ResourceMetadataPatchRequest req, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthorizationContext();

        if (!await twinView.CanWriteResourceAsync(auth, resourceType, resourceId, ct).ConfigureAwait(false))
            return Forbid();

        var exists = await ResourceExistsAsync(resourceType, resourceId, ct).ConfigureAwait(false);
        if (!exists) return NotFound();

        // Non-point resources: the URL param is the dtId (RDF IRI) — use it directly as SPARQL subject.
        await db.UpdateResourceMetadataAsync(resourceId, req.Identifiers, req.CustomTags, ct).ConfigureAwait(false);
        return NoContent();
    }

    private async Task<bool> ResourceExistsAsync(string resourceType, string resourceId, CancellationToken ct)
        => resourceType switch
        {
            "building" => await db.GetBuilding(resourceId).ConfigureAwait(false) is not null,
            "floor"    => await db.GetFloor(resourceId).ConfigureAwait(false) is not null,
            "space"    => await db.GetSpace(resourceId).ConfigureAwait(false) is not null,
            "device"   => await db.GetDevice(resourceId).ConfigureAwait(false) is not null,
            _          => false,
        };

    private static ResourceMetadataResponse MetadataOf(Dictionary<string, string> identifiers, Dictionary<string, bool> customTags)
        => new() { Identifiers = identifiers, CustomTags = customTags };
}

public class ResourceMetadataResponse
{
    public Dictionary<string, string> Identifiers { get; set; } = new();
    public Dictionary<string, bool> CustomTags { get; set; } = new();
}

public class ResourceMetadataPatchRequest
{
    public Dictionary<string, string?>? Identifiers { get; set; }
    public Dictionary<string, bool?>? CustomTags { get; set; }
}
