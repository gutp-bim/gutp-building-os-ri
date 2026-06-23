using BuildingOS.Shared;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Route("/resources")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status200OK)]
[AuthorizeFilter]
public class ResourceSearchController(IAuthorizedTwinView twinView) : ControllerBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    /// <summary>
    /// リソース横断検索（building / floor / space / device / point を名前・IDで検索）。
    /// </summary>
    /// <param name="q">検索語（名前・ID の部分一致、大文字小文字を無視）</param>
    /// <param name="type">リソース種別で絞り込み（building/floor/space/device/point）。省略時は全種別</param>
    /// <param name="buildingId">建物 dtId でスコープ（building/floor/space のみ対象）</param>
    /// <param name="tag">SBCO customTags のキーで絞り込み（customTags[key] == true）。複数指定は AND（#332）</param>
    /// <param name="limit">最大件数（1..200、既定 50）</param>
    /// <param name="offset">オフセット（既定 0）</param>
    [HttpGet("search")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ResourceSearchHit[]>> Search(
        [FromQuery] string? q,
        [FromQuery] string? type,
        [FromQuery] string? buildingId,
        [FromQuery] string[]? tag,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (offset < 0) return BadRequest("offset must be >= 0");
        limit = Math.Clamp(limit, 1, MaxLimit);

        // customTags AND filter (#332): customTags[tag] == true per tag. Blank entries are ignored.
        var tags = (tag ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();

        var hits = await twinView.SearchAsync(
            HttpContext.GetAuthorizationContext(), q, type, buildingId, tags, limit, offset, ct);
        return Ok(hits);
    }
}
