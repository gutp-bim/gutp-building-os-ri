namespace BuildingOs.ApiServer.Controllers;

using BuildingOs.ApiServer.Extensions;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Grouping.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// リソースグループ管理API（admin専用）
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class GroupsController : ControllerBase
{
    private readonly IGroupRepository _groupRepository;
    private readonly ILogger<GroupsController> _logger;

    public GroupsController(IGroupRepository groupRepository, ILogger<GroupsController> logger)
    {
        _groupRepository = groupRepository;
        _logger = logger;
    }

    // === Group CRUD ===

    /// <summary>
    /// グループ一覧を取得
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<GroupResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<GroupResponse>>> GetAll(CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();
        var groups = await _groupRepository.GetAllAsync(ct).ConfigureAwait(false);
        return Ok(groups.Select(ToResponse));
    }

    /// <summary>
    /// グループ詳細を取得（リソースアイテム含む）
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(GroupDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GroupDetailResponse>> GetById(string id, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var group = await _groupRepository.GetByIdWithItemsAsync(id, ct).ConfigureAwait(false);
        if (group == null)
        {
            return NotFound();
        }
        return Ok(ToDetailResponse(group));
    }

    /// <summary>
    /// グループを作成
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(GroupResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GroupResponse>> Create([FromBody] CreateGroupRequest request, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Id and Name are required");
        }

        var existing = await _groupRepository.GetByIdAsync(request.Id, ct).ConfigureAwait(false);
        if (existing != null)
        {
            return BadRequest($"Group with id '{request.Id}' already exists");
        }

        var group = new ResourceGroup
        {
            Id = request.Id,
            Name = request.Name,
            Description = request.Description
        };

        var created = await _groupRepository.CreateAsync(group, ct).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ToResponse(created));
    }

    /// <summary>
    /// グループを更新
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Update(string id, [FromBody] UpdateGroupRequest request, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var existing = await _groupRepository.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return NotFound();
        }

        existing.Name = request.Name ?? existing.Name;
        existing.Description = request.Description;

        await _groupRepository.UpdateAsync(existing, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// グループを削除
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(string id, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var existing = await _groupRepository.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return NotFound();
        }

        await _groupRepository.DeleteAsync(id, ct).ConfigureAwait(false);
        return NoContent();
    }

    // === ResourceItem ===

    /// <summary>
    /// グループにリソースを追加
    /// </summary>
    [HttpPost("{id}/resources")]
    [ProducesResponseType(typeof(ResourceItemResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ResourceItemResponse>> AddResource(
        string id,
        [FromBody] AddResourceRequest request,
        CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var group = await _groupRepository.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (group == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.ResourceType) || string.IsNullOrWhiteSpace(request.ResourceId))
        {
            return BadRequest("ResourceType and ResourceId are required");
        }

        try
        {
            var item = await _groupRepository.AddResourceItemAsync(
                id, request.ResourceType, request.ResourceId, ct).ConfigureAwait(false);
            return CreatedAtAction(nameof(GetById), new { id }, ToResourceItemResponse(item));
        }
        catch (Exception ex) when (ex.InnerException?.Message?.Contains("duplicate") == true ||
                                   ex.InnerException?.Message?.Contains("unique") == true)
        {
            return BadRequest("Resource already exists in this group");
        }
    }

    /// <summary>
    /// グループからリソースを削除
    /// </summary>
    [HttpDelete("{id}/resources/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveResource(string id, string itemId, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var group = await _groupRepository.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (group == null)
        {
            return NotFound();
        }

        await _groupRepository.RemoveResourceItemAsync(itemId, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// グループにリソースを一括追加
    /// </summary>
    [HttpPost("{id}/resources/bulk")]
    [ProducesResponseType(typeof(BulkAddResourceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BulkAddResourceResponse>> AddResourcesBulk(
        string id,
        [FromBody] BulkAddResourceRequest request,
        CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var group = await _groupRepository.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (group == null)
        {
            return NotFound();
        }

        var added = new List<ResourceItemResponse>();
        var failed = new List<string>();

        foreach (var item in request.Items)
        {
            try
            {
                var created = await _groupRepository.AddResourceItemAsync(
                    id, item.ResourceType, item.ResourceId, ct).ConfigureAwait(false);
                added.Add(ToResourceItemResponse(created));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add resource {ResourceType}:{ResourceId} to group {GroupId}",
                    item.ResourceType, item.ResourceId, id);
                failed.Add($"{item.ResourceType}:{item.ResourceId}");
            }
        }

        return Ok(new BulkAddResourceResponse { Added = added, Failed = failed });
    }

    // === Response/Request DTOs ===

    private static GroupResponse ToResponse(ResourceGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        Description = group.Description,
        CreatedAt = group.CreatedAt,
        UpdatedAt = group.UpdatedAt
    };

    private static GroupDetailResponse ToDetailResponse(ResourceGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        Description = group.Description,
        CreatedAt = group.CreatedAt,
        UpdatedAt = group.UpdatedAt,
        ResourceItems = group.ResourceItems.Select(ToResourceItemResponse).ToList()
    };

    private static ResourceItemResponse ToResourceItemResponse(GroupResourceItem item) => new()
    {
        Id = item.Id,
        ResourceType = item.ResourceType,
        ResourceId = item.ResourceId,
        CreatedAt = item.CreatedAt
    };

    // === Request Models ===

    public record CreateGroupRequest
    {
        public string Id { get; init; } = default!;
        public string Name { get; init; } = default!;
        public string? Description { get; init; }
    }

    public record UpdateGroupRequest
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
    }

    public record AddResourceRequest
    {
        public string ResourceType { get; init; } = default!;
        public string ResourceId { get; init; } = default!;
    }

    public record BulkAddResourceRequest
    {
        public List<AddResourceRequest> Items { get; init; } = [];
    }

    // === Response Models ===

    public record GroupResponse
    {
        public string Id { get; init; } = default!;
        public string Name { get; init; } = default!;
        public string? Description { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    public record GroupDetailResponse : GroupResponse
    {
        public List<ResourceItemResponse> ResourceItems { get; init; } = [];
    }

    public record ResourceItemResponse
    {
        public string Id { get; init; } = default!;
        public string ResourceType { get; init; } = default!;
        public string ResourceId { get; init; } = default!;
        public DateTime CreatedAt { get; init; }
    }

    public record BulkAddResourceResponse
    {
        public List<ResourceItemResponse> Added { get; init; } = [];
        public List<string> Failed { get; init; } = [];
    }
}
