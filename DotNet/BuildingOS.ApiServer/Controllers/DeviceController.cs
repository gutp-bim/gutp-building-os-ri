using System.Diagnostics;
using BuildingOS.Shared;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

[ApiController]
[Route("/devices")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status200OK)]
[AuthorizeFilter]
public class DeviceController(IAuthorizedTwinView twinView) : ControllerBase
{
    /// <summary>
    /// デバイス情報の取得
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Route("{deviceDtId}")]
    public async Task<ActionResult<Device>> Get(string deviceDtId, CancellationToken ct)
        => await twinView.GetDeviceAsync(HttpContext.GetAuthorizationContext(), Uri.UnescapeDataString(deviceDtId), ct) switch
        {
            TwinGetResult<Device>.Ok ok      => ok.Resource,
            TwinGetResult<Device>.Forbidden  => Forbid(),
            TwinGetResult<Device>.NotFound   => NotFound(),
            _                                => throw new UnreachableException()
        };

    /// <summary>
    /// デバイスの一括取得
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Device[]>> List([FromQuery] string? spaceDtId, CancellationToken ct)
    {
        var auth = HttpContext.GetAuthorizationContext();
        if (string.IsNullOrEmpty(spaceDtId) && !auth.IsAdmin) return Forbid();
        return await twinView.ListDevicesAsync(auth, spaceDtId, ct);
    }
}
