using System.Diagnostics;
using BuildingOS.Shared;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Route("/floors")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status200OK)]
[AuthorizeFilter]
public class FloorController(IAuthorizedTwinView twinView) : ControllerBase
{
    /// <summary>
    /// フロア情報の取得
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Route("{floorDtId}")]
    public async Task<ActionResult<Floor>> Get(string floorDtId, CancellationToken ct)
        => await twinView.GetFloorAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(floorDtId), ct) switch
        {
            TwinGetResult<Floor>.Ok ok      => ok.Resource,
            TwinGetResult<Floor>.Forbidden  => Forbid(),
            TwinGetResult<Floor>.NotFound   => NotFound(),
            _                               => throw new UnreachableException()
        };

    /// <summary>
    /// フロア情報の一括取得
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Floor[]>> List([FromQuery] string? buildingDtId, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthorizationContext();
        if (string.IsNullOrEmpty(buildingDtId) && !auth.IsAdmin) return Forbid();
        return await twinView.ListFloorsAsync(auth, buildingDtId, ct);
    }
}
