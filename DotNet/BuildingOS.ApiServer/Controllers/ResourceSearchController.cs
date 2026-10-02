using BuildingOs.ApiServer.Routing;
using BuildingOS.Shared;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Route(ApiRoutes.V1 + "/resources")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status200OK)]
[AuthorizeFilter]
public class ResourceSearchController(IAuthorizedTwinView twinView) : ControllerBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int DefaultTagLimit = 20;
    private const int MaxTagLimit = 100;

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

    /// <summary>
    /// customTags 候補（autocomplete 用）。キー（true のもの）を prefix 前方一致（大文字小文字無視）で集計し、
    /// 呼び出し元が閲覧できるリソースの件数付きで多い順に返す。group-manager には空（タグは非公開）。
    /// </summary>
    /// <param name="prefix">キーの前方一致。省略時は全キー</param>
    /// <param name="limit">最大件数（1..100、既定 20）</param>
    [HttpGet("tags")]
    public async Task<ActionResult<ResourceTagCount[]>> Tags(
        [FromQuery] string? prefix,
        [FromQuery] int limit = DefaultTagLimit,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxTagLimit);
        var tags = await twinView.ListTagsAsync(HttpContext.GetAuthorizationContext(), prefix?.Trim(), limit, ct);
        return Ok(tags);
    }
}
