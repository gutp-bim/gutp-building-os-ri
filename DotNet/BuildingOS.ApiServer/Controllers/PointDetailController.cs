using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;
using AuthorizationService = BuildingOS.Shared.Domain.Authorization.IAuthorizationService;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status200OK)]
[AuthorizeFilter]
public class PointDetailController(
    IDigitalTwinDatabase digitalTwinDatabase,
    IControlSchemaResolver controlSchemaResolver,
    AuthorizationService authorizationService
) : ControllerBase
{
    /// <summary>
    /// ポイント詳細の一括取得
    /// </summary>
    /// <param name="buildingDtId">ビルのdtId</param>
    /// <returns></returns>
    [HttpGet]
    [Route("point-details")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PointDetail[]>> List([FromQuery] string buildingDtId, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();

        if (!authContext.IsAdmin)
        {
            var canAccess = await authorizationService.CanAccessAsync(
                authContext, "building", Uri.UnescapeDataString(buildingDtId), "read", ct).ConfigureAwait(false);
            if (!canAccess) return Forbid();
        }

        try
        {
            var result = await digitalTwinDatabase.ListPointDetails(Uri.UnescapeDataString(buildingDtId));

            foreach (var pd in result)
            {
                pd.ControlSchema = await controlSchemaResolver.ResolveAsync(pd.Point, pd.Device);
            }

            return Ok(result);
        }
        catch (DigitalTwinNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// ポイント詳細の取得
    /// </summary>
    /// <param name="pointId">ポイントID（ビジネスID）</param>
    /// <returns></returns>
    [HttpGet]
    [Route("point-details/{pointId}")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PointDetail>> Get(string pointId, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();

        if (!authContext.IsAdmin)
        {
            var canAccess = await authorizationService.CanAccessAsync(
                authContext, "point", Uri.UnescapeDataString(pointId), "read", ct).ConfigureAwait(false);
            if (!canAccess) return Forbid();
        }

        var result = await digitalTwinDatabase.GetPointDetailByPointId(Uri.UnescapeDataString(pointId));
        if (result == null) return NotFound();

        result.ControlSchema = await controlSchemaResolver.ResolveAsync(result.Point, result.Device);

        return Ok(result);
    }
}
