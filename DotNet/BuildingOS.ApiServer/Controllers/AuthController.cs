using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// 認可コンテキストの確認用エンドポイント
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AuthorizeFilter]
public class AuthController : ControllerBase
{
    private readonly BuildingOS.Shared.Domain.Authorization.IAuthorizationService _authorizationService;

    public AuthController(BuildingOS.Shared.Domain.Authorization.IAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    /// <summary>
    /// 現在のユーザーの認可コンテキストを取得
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetMe()
    {
        var authContext = HttpContext.GetAuthorizationContext();

        return Ok(new
        {
            authContext.UserId,
            authContext.Role,
            authContext.IsAdmin,
            authContext.Permissions,
            Claims = HttpContext.User.Claims.Select(c => new { c.Type, c.Value }).ToList()
        });
    }

    /// <summary>
    /// 指定リソースへのアクセス可否を確認
    /// </summary>
    [HttpGet("check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckAccess(
        [FromQuery] string resourceType,
        [FromQuery] string resourceId,
        [FromQuery] string action,
        CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        var canAccess = await _authorizationService.CanAccessAsync(
            authContext, resourceType, resourceId, action, ct).ConfigureAwait(false);

        return Ok(new
        {
            CanAccess = canAccess,
            authContext.UserId,
            authContext.Role,
            authContext.IsAdmin,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Action = action,
            authContext.Permissions
        });
    }
}
