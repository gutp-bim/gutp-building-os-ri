using BuildingOS.Shared.Domain.Grouping;
using Microsoft.EntityFrameworkCore;

namespace BuildingOS.Shared.Domain.Authorization;

/// <summary>
/// ハッシュ化されたリソースID → 元IDのマッピングリポジトリ実装
/// </summary>
public class ResourceIdMappingRepository : IResourceIdMappingRepository
{
    private readonly RelationalDbContext _context;

    public ResourceIdMappingRepository(RelationalDbContext context)
    {
        _context = context;
    }

    public async Task SaveMappingAsync(string resourceType, string originalId, string? displayName = null, CancellationToken ct = default)
    {
        var hashedId = PermissionHelper.HashResourceId(originalId);

        var existing = await _context.ResourceIdMappings
            .FirstOrDefaultAsync(m => m.HashedId == hashedId, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            _context.ResourceIdMappings.Add(new ResourceIdMapping
            {
                HashedId = hashedId,
                ResourceType = resourceType,
                OriginalId = originalId,
                DisplayName = displayName,
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else if (displayName != null && existing.DisplayName != displayName)
        {
            existing.DisplayName = displayName;
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveOriginalIdsAsync(
        IEnumerable<string> hashedIds, CancellationToken ct = default)
    {
        var hashList = hashedIds.ToList();
        if (hashList.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        return await _context.ResourceIdMappings
            .AsNoTracking()
            .Where(m => hashList.Contains(m.HashedId))
            .ToDictionaryAsync(m => m.HashedId, m => m.OriginalId, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ResourceIdMapping>> ResolveMappingsAsync(
        IEnumerable<string> hashedIds, CancellationToken ct = default)
    {
        var hashList = hashedIds.ToList();
        if (hashList.Count == 0)
        {
            return [];
        }

        return await _context.ResourceIdMappings
            .AsNoTracking()
            .Where(m => hashList.Contains(m.HashedId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
