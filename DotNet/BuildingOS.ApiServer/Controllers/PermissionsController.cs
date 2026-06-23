namespace BuildingOs.ApiServer.Controllers;

using BuildingOs.ApiServer.Extensions;
using BuildingOS.Shared.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// パーミッション解決API（admin専用）
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class PermissionsController : ControllerBase
{
    private readonly IResourceIdMappingRepository _mappingRepository;

    public PermissionsController(IResourceIdMappingRepository mappingRepository)
    {
        _mappingRepository = mappingRepository;
    }

    /// <summary>
    /// ハッシュ化されたリソースIDを元のID・リソースタイプ・表示名に解決する
    /// </summary>
    [HttpPost("resolve")]
    [ProducesResponseType(typeof(Dictionary<string, ResolvedPermissionInfo>), StatusCodes.Status200OK)]
    public async Task<ActionResult<Dictionary<string, ResolvedPermissionInfo>>> Resolve(
        [FromBody] string[] hashedIds, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var mappings = await _mappingRepository.ResolveMappingsAsync(hashedIds, ct).ConfigureAwait(false);

        var result = mappings.ToDictionary(
            m => m.HashedId,
            m => new ResolvedPermissionInfo
            {
                OriginalId = m.OriginalId,
                ResourceType = m.ResourceType,
                DisplayName = m.DisplayName
            });

        return Ok(result);
    }

    public record ResolvedPermissionInfo
    {
        public string OriginalId { get; init; } = default!;
        public string ResourceType { get; init; } = default!;
        public string? DisplayName { get; init; }
    }
}
