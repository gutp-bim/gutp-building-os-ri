namespace BuildingOS.Shared.Domain.Grouping;

using BuildingOS.Shared.Domain.Grouping.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// リソースグループのリポジトリ実装
/// </summary>
public class GroupRepository : IGroupRepository
{
    private readonly RelationalDbContext _context;
    private readonly ILogger<GroupRepository> _logger;

    public GroupRepository(RelationalDbContext context, ILogger<GroupRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    // === Group CRUD ===

    public async Task<ResourceGroup?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        return await _context.ResourceGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<ResourceGroup?> GetByIdWithItemsAsync(string id, CancellationToken ct = default)
    {
        return await _context.ResourceGroups
            .AsNoTracking()
            .Include(g => g.ResourceItems)
            .FirstOrDefaultAsync(g => g.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ResourceGroup>> GetAllAsync(CancellationToken ct = default)
    {
        return await _context.ResourceGroups
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<ResourceGroup> CreateAsync(ResourceGroup group, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        group.CreatedAt = now;
        group.UpdatedAt = now;

        _context.ResourceGroups.Add(group);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Created resource group {GroupId} with name {GroupName}", group.Id, group.Name);

        return group;
    }

    public async Task UpdateAsync(ResourceGroup group, CancellationToken ct = default)
    {
        var existing = await _context.ResourceGroups
            .FirstOrDefaultAsync(g => g.Id == group.Id, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            throw new InvalidOperationException($"Resource group {group.Id} not found");
        }

        existing.Name = group.Name;
        existing.Description = group.Description;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Updated resource group {GroupId}", group.Id);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var group = await _context.ResourceGroups
            .FirstOrDefaultAsync(g => g.Id == id, ct)
            .ConfigureAwait(false);

        if (group != null)
        {
            _context.ResourceGroups.Remove(group);
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("Deleted resource group {GroupId}", id);
        }
    }

    // === ResourceItem ===

    public async Task<GroupResourceItem> AddResourceItemAsync(
        string groupId,
        string resourceType,
        string resourceId,
        CancellationToken ct = default)
    {
        var item = new GroupResourceItem
        {
            Id = Guid.NewGuid().ToString("N"),
            GroupId = groupId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            CreatedAt = DateTime.UtcNow
        };

        _context.GroupResourceItems.Add(item);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Added resource item {ResourceType}:{ResourceId} to group {GroupId}",
            resourceType, resourceId, groupId);

        return item;
    }

    public async Task RemoveResourceItemAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _context.GroupResourceItems
            .FirstOrDefaultAsync(i => i.Id == itemId, ct)
            .ConfigureAwait(false);

        if (item != null)
        {
            _context.GroupResourceItems.Remove(item);
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Removed resource item {ItemId} ({ResourceType}:{ResourceId}) from group {GroupId}",
                itemId, item.ResourceType, item.ResourceId, item.GroupId);
        }
    }

    public async Task<IReadOnlyList<GroupResourceItem>> GetResourceItemsAsync(string groupId, CancellationToken ct = default)
    {
        return await _context.GroupResourceItems
            .AsNoTracking()
            .Where(i => i.GroupId == groupId)
            .OrderBy(i => i.ResourceType)
            .ThenBy(i => i.ResourceId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    // === 逆引き ===

    public async Task<IReadOnlyList<string>> GetGroupIdsForResourceAsync(
        string resourceType,
        string resourceId,
        CancellationToken ct = default)
    {
        return await _context.GroupResourceItems
            .AsNoTracking()
            .Where(i => i.ResourceType == resourceType && i.ResourceId == resourceId)
            .Select(i => i.GroupId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetResourceIdsInGroupAsync(
        string groupId,
        string resourceType,
        CancellationToken ct = default)
    {
        return await _context.GroupResourceItems
            .AsNoTracking()
            .Where(i => i.GroupId == groupId && i.ResourceType == resourceType)
            .Select(i => i.ResourceId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
