using System.Diagnostics;
using BuildingOS.Shared;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Route("/spaces")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status200OK)]
[AuthorizeFilter]
public class SpaceController(IAuthorizedTwinView twinView) : ControllerBase
{
    /// <summary>
    /// スペース情報の取得
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Route("{spaceDtId}")]
    public async Task<ActionResult<Space>> Get(string spaceDtId, CancellationToken ct)
        => await twinView.GetSpaceAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(spaceDtId), ct) switch
        {
            TwinGetResult<Space>.Ok ok      => ok.Resource,
            TwinGetResult<Space>.Forbidden  => Forbid(),
            TwinGetResult<Space>.NotFound   => NotFound(),
            _                               => throw new UnreachableException()
        };

    // Route note (#440): the PRD sketched this as /resources/{roomId}/adjacent-rooms, but /resources
    // is the cross-resource search façade (ResourceSearchController) — per-type resource reads live
    // on their own controller, which is also where the Space authorization path already is. Hence
    // /spaces/{spaceDtId}/adjacent-spaces, which additionally inherits this class's [AuthorizeFilter].
    /// <summary>
    /// 隣接スペースの取得
    /// </summary>
    /// <remarks>
    /// 指定した部屋に隣接する部屋（`sbco:Room`）の一覧。隣接関係は BOT の対称関係
    /// `bot:adjacentZone` に由来し、取り込み時に双方向へ正規化されている。
    /// 読み取り権限のない隣室は結果から除外される。部屋自体が存在しない場合は 404。
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Route("{spaceDtId}/adjacent-spaces")]
    public async Task<ActionResult<Space[]>> GetAdjacentSpaces(string spaceDtId, CancellationToken ct)
        => await twinView.ListAdjacentSpacesAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(spaceDtId), ct) switch
        {
            TwinGetResult<Space[]>.Ok ok     => ok.Resource,
            TwinGetResult<Space[]>.Forbidden => Forbid(),
            TwinGetResult<Space[]>.NotFound  => NotFound(),
            _                                => throw new UnreachableException()
        };

    /// <summary>
    /// スペース情報の一括取得
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Space[]>> List([FromQuery] string? floorDtId, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthorizationContext();
        if (string.IsNullOrEmpty(floorDtId) && !auth.IsAdmin) return Forbid();
        return await twinView.ListSpacesAsync(auth, floorDtId, ct);
    }
}
