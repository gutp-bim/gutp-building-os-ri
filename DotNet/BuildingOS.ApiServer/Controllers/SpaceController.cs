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
