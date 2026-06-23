using System.Diagnostics;
using BuildingOS.Shared;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Route("/buildings")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status200OK)]
[AuthorizeFilter]
public class BuildingController(IAuthorizedTwinView twinView) : ControllerBase
{
    /// <summary>
    /// ビル情報の一括取得
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Building[]>> List(CancellationToken ct)
        => await twinView.ListBuildingsAsync(HttpContext.GetAuthorizationContext(), ct);

    /// <summary>
    /// ビル情報を取得
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Route("{buildingDtId}")]
    public async Task<ActionResult<Building>> Get(string buildingDtId, CancellationToken ct)
        => await twinView.GetBuildingAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(buildingDtId), ct) switch
        {
            TwinGetResult<Building>.Ok ok       => ok.Resource,
            TwinGetResult<Building>.Forbidden   => Forbid(),
            TwinGetResult<Building>.NotFound    => NotFound(),
            _                                   => throw new UnreachableException()
        };
}
