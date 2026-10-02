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
    /// <param name="deviceType">sbco:deviceType で絞り込み（機器、または機器に属するポイント）。複数指定は OR</param>
    /// <param name="pointType">sbco:pointType で絞り込み（ポイント）。複数指定は OR</param>
    /// <param name="unit">sbco:unit で絞り込み（ポイント）。複数指定は OR</param>
    /// <param name="gatewayId">sbco:gatewayId で絞り込み（ポイント）。複数指定は OR</param>
    [HttpGet("search")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ResourceSearchHit[]>> Search(
        [FromQuery] string? q,
        [FromQuery] string? type,
        [FromQuery] string? buildingId,
        [FromQuery] string[]? tag,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] int offset = 0,
        [FromQuery] string[]? deviceType = null,
        [FromQuery] string[]? pointType = null,
        [FromQuery] string[]? unit = null,
        [FromQuery] string[]? gatewayId = null,
        CancellationToken ct = default)
    {
        if (offset < 0) return BadRequest("offset must be >= 0");
        limit = Math.Clamp(limit, 1, MaxLimit);

        var tags = NonBlank(tag);
        var attrs = Attributes(deviceType, pointType, unit, gatewayId);
        var auth = HttpContext.GetAuthorizationContext();

        // Attribute filters take the filtered path; without them the established search path is untouched.
        var hits = attrs.IsEmpty
            ? await twinView.SearchAsync(auth, q, type, buildingId, tags, limit, offset, ct)
            : await twinView.SearchFilteredAsync(auth, q, type, buildingId, tags, attrs, limit, offset, ct);
        return Ok(hits);
    }

    /// <summary>
    /// 検索結果に対する facet 集計（種別 / 機器種別 / 計測種別 / 単位 / Gateway ごとの件数）。
    /// <c>search</c> と同じ絞り込み条件を受け取り、呼び出し元が閲覧できるリソースだけを数える。
    /// 件数は「現在の結果」に対するもの（選択済みの facet を含む）。group-manager には空。
    /// </summary>
    /// <param name="q">検索語（名前・ID の部分一致）</param>
    /// <param name="type">リソース種別で絞り込み</param>
    /// <param name="buildingId">建物 dtId でスコープ</param>
    /// <param name="tag">customTags のキー。複数指定は AND</param>
    /// <param name="deviceType">sbco:deviceType。複数指定は OR</param>
    /// <param name="pointType">sbco:pointType。複数指定は OR</param>
    /// <param name="unit">sbco:unit。複数指定は OR</param>
    /// <param name="gatewayId">sbco:gatewayId。複数指定は OR</param>
    [HttpGet("facets")]
    public async Task<ActionResult<ResourceFacets>> Facets(
        [FromQuery] string? q,
        [FromQuery] string? type,
        [FromQuery] string? buildingId,
        [FromQuery] string[]? tag,
        [FromQuery] string[]? deviceType = null,
        [FromQuery] string[]? pointType = null,
        [FromQuery] string[]? unit = null,
        [FromQuery] string[]? gatewayId = null,
        CancellationToken ct = default)
    {
        var facets = await twinView.GetFacetsAsync(
            HttpContext.GetAuthorizationContext(), q, type, buildingId,
            NonBlank(tag), Attributes(deviceType, pointType, unit, gatewayId), ct);
        return Ok(facets);
    }

    // Blank entries are ignored so a stray "?tag=" does not become a constraint.
    private static string[] NonBlank(string[]? values) =>
        (values ?? Array.Empty<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();

    private static ResourceAttributeFilter Attributes(string[]? deviceType, string[]? pointType, string[]? unit, string[]? gatewayId) =>
        new(NonBlank(deviceType), NonBlank(pointType), NonBlank(unit), NonBlank(gatewayId));

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
