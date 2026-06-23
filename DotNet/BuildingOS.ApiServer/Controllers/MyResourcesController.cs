using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// ユーザーのアクセス可能リソース取得エンドポイント
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AuthorizeFilter]
public class MyResourcesController : ControllerBase
{
    private readonly BuildingOS.Shared.Domain.Authorization.IAuthorizationService _authorizationService;
    private readonly IResourceIdMappingRepository _mappingRepository;

    public MyResourcesController(
        BuildingOS.Shared.Domain.Authorization.IAuthorizationService authorizationService,
        IResourceIdMappingRepository mappingRepository)
    {
        _authorizationService = authorizationService;
        _mappingRepository = mappingRepository;
    }

    /// <summary>
    /// 指定リソースタイプのアクセス可能リソースID一覧を取得
    /// </summary>
    [HttpGet("accessible")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAccessible(
        [FromQuery] string resourceType,
        [FromQuery] string action,
        CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        var hashedIds = await _authorizationService.GetAccessibleResourceIdsAsync(
            authContext, resourceType, action, ct).ConfigureAwait(false);

        // ハッシュIDを元IDに逆引き
        var originalIds = await ResolveToOriginalIdsAsync(hashedIds, ct).ConfigureAwait(false);

        return Ok(new
        {
            authContext.UserId,
            authContext.Role,
            authContext.IsAdmin,
            ResourceType = resourceType,
            Action = action,
            AccessibleResourceIds = originalIds
        });
    }

    /// <summary>
    /// ユーザーのアクセス可能リソース一覧を取得（全リソースタイプ）
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(MyResourcesResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyResources(CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();

        if (authContext.IsAdmin)
        {
            return Ok(new MyResourcesResponse { IsAdmin = true });
        }

        var resourceTypes = new[] { "building", "floor", "space", "device", "point" };
        var resources = new Dictionary<string, IReadOnlyList<string>>();

        foreach (var type in resourceTypes)
        {
            var hashedIds = await _authorizationService.GetAccessibleResourceIdsAsync(
                authContext, type, "read", ct).ConfigureAwait(false);

            // ハッシュIDを元IDに逆引き
            var originalIds = await ResolveToOriginalIdsAsync(hashedIds, ct).ConfigureAwait(false);
            resources[type] = originalIds;
        }

        return Ok(new MyResourcesResponse { IsAdmin = false, Resources = resources });
    }

    /// <summary>
    /// ハッシュ化されたリソースIDリストを元のIDリストに変換する
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveToOriginalIdsAsync(
        IReadOnlyList<string> hashedIds, CancellationToken ct)
    {
        if (hashedIds.Count == 0) return hashedIds;

        var mapping = await _mappingRepository.ResolveOriginalIdsAsync(hashedIds, ct).ConfigureAwait(false);

        return hashedIds
            .Select(h => mapping.TryGetValue(h, out var original) ? original : h)
            .ToList();
    }
}

public class MyResourcesResponse
{
    public bool IsAdmin { get; set; }
    public Dictionary<string, IReadOnlyList<string>>? Resources { get; set; }
}
