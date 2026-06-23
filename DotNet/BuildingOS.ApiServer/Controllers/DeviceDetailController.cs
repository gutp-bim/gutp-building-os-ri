using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using AuthorizationService = BuildingOS.Shared.Domain.Authorization.IAuthorizationService;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status200OK)]
[AuthorizeFilter]
public class DeviceDetailController(IDigitalTwinDatabase digitalTwinDatabase, AuthorizationService authorizationService) : ControllerBase
{
    /// <summary>
    /// ビル内のデバイス詳細情報の一括取得
    /// </summary>
    /// <param name="buildingDtId">ビルのdtId</param>
    /// <returns></returns>
    [HttpGet]
    [Route("device-details")]
    public async Task<ActionResult<DeviceDetail[]>> List([FromQuery] string buildingDtId, CancellationToken ct)
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
            var result = await digitalTwinDatabase.ListDeviceDetails(Uri.UnescapeDataString(buildingDtId));
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
}
