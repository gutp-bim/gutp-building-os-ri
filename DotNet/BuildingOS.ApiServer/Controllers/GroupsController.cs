using BuildingOs.ApiServer.Routing;
namespace BuildingOs.ApiServer.Controllers;

using BuildingOs.ApiServer.Extensions;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Grouping.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// リソースグループ管理API（admin と group-manager、#506）
/// </summary>
[ApiController]
[Route(ApiRoutes.V1 + "/groups")]
[Authorize]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class GroupsController : ControllerBase
{
    private readonly IGroupRepository _groupRepository;
    private readonly ILogger<GroupsController> _logger;
    private readonly IAdminAuditRecorder? _audit;

    public GroupsController(
        IGroupRepository groupRepository, ILogger<GroupsController> logger, IAdminAuditRecorder? audit = null)
    {
        _groupRepository = groupRepository;
        _logger = logger;
        _audit = audit;
    }

    // #506: a Group's items are access grants for every user holding group:<id>:<actions>, so a
    // group-manager only sees and changes the Groups it created itself — never an admin's Group that
    // users may hold write on. Someone else's Group answers 404, as if it did not exist. Groups from
    // before ownership was recorded (CreatedBy null) are admin-only.
    private static bool Manages(AuthorizationContext auth, ResourceGroup group)
        => auth.IsAdmin || (auth.CanManageGroups && group.CreatedBy == auth.UserId);

    private async Task<ResourceGroup?> LoadManagedAsync(
        AuthorizationContext auth, string id, bool withItems, CancellationToken ct)
    {
        var group = withItems
            ? await _groupRepository.GetByIdWithItemsAsync(id, ct).ConfigureAwait(false)
            : await _groupRepository.GetByIdAsync(id, ct).ConfigureAwait(false);
        return group is not null && Manages(auth, group) ? group : null;
    }

    private Task AuditAsync(AuthorizationContext auth, string action, string targetId, object? detail, CancellationToken ct)
    {
        if (_audit is null) return Task.CompletedTask;
        var detailJson = detail is null ? null : System.Text.Json.JsonSerializer.Serialize(detail);
        return _audit.RecordAsync(
            AdminAuditRecord.Create(AdminAuditSubjects.Group, action, targetId, auth.UserId, actorName: null,
                AdminAuditResult.Success, detailJson),
            ct);
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
        if (!authContext.CanManageGroups) return Forbid();
        var groups = await _groupRepository.GetAllAsync(ct).ConfigureAwait(false);
        return Ok(groups.Where(g => Manages(authContext, g)).Select(ToResponse));
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
        if (!authContext.CanManageGroups) return Forbid();

        var group = await LoadManagedAsync(authContext, id, withItems: true, ct).ConfigureAwait(false);
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
        if (!authContext.CanManageGroups) return Forbid();

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
            Description = request.Description,
            CreatedBy = authContext.UserId,
        };

        var created = await _groupRepository.CreateAsync(group, ct).ConfigureAwait(false);
        await AuditAsync(authContext, "group-create", created.Id, new { name = created.Name }, ct).ConfigureAwait(false);
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
        if (!authContext.CanManageGroups) return Forbid();

        var existing = await LoadManagedAsync(authContext, id, withItems: false, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return NotFound();
        }

        existing.Name = request.Name ?? existing.Name;
        existing.Description = request.Description;

        await _groupRepository.UpdateAsync(existing, ct).ConfigureAwait(false);
        await AuditAsync(authContext, "group-update", id, new { name = existing.Name }, ct).ConfigureAwait(false);
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
        if (!authContext.CanManageGroups) return Forbid();

        var existing = await LoadManagedAsync(authContext, id, withItems: false, ct).ConfigureAwait(false);
        if (existing == null)
        {
            return NotFound();
        }

        await _groupRepository.DeleteAsync(id, ct).ConfigureAwait(false);
        await AuditAsync(authContext, "group-delete", id, null, ct).ConfigureAwait(false);
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
        if (!authContext.CanManageGroups) return Forbid();

        var group = await LoadManagedAsync(authContext, id, withItems: false, ct).ConfigureAwait(false);
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
            await AuditAsync(authContext, "group-add-resource", id,
                new { resourceType = request.ResourceType, resourceId = request.ResourceId }, ct).ConfigureAwait(false);
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
        if (!authContext.CanManageGroups) return Forbid();

        var group = await LoadManagedAsync(authContext, id, withItems: true, ct).ConfigureAwait(false);
        // The item must belong to the Group in the route; otherwise a caller managing one Group could
        // remove items from any other by naming its item id (#506 review).
        var item = group?.ResourceItems.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            return NotFound();
        }

        await _groupRepository.RemoveResourceItemAsync(itemId, ct).ConfigureAwait(false);
        await AuditAsync(authContext, "group-remove-resource", id,
            new { resourceType = item.ResourceType, resourceId = item.ResourceId }, ct).ConfigureAwait(false);
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
        if (!authContext.CanManageGroups) return Forbid();

        var group = await LoadManagedAsync(authContext, id, withItems: false, ct).ConfigureAwait(false);
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

        await AuditAsync(authContext, "group-add-resources-bulk", id,
            new { added = added.Select(a => $"{a.ResourceType}:{a.ResourceId}"), failed }, ct).ConfigureAwait(false);
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
