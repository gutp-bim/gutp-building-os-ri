using BuildingOS.Shared.Domain.Authorization;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// No-op stubs for authorization interfaces, available as fallbacks when
/// POSTGRES_CONNECTION_STRING is not configured (e.g. minimal OSS dev setup).
/// </summary>
public sealed class NoOpGroupMembershipResolver : IGroupMembershipResolver
{
    public Task<IReadOnlyList<string>> GetGroupsContainingResourceAsync(string resourceType, string resourceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<IReadOnlyList<string>> GetGroupMembersAsync(string groupId, string resourceType, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

public sealed class NoOpResourceIdMappingRepository : IResourceIdMappingRepository
{
    public Task SaveMappingAsync(string resourceType, string originalId, string? displayName = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, string>> ResolveOriginalIdsAsync(IEnumerable<string> hashedIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    public Task<IReadOnlyList<ResourceIdMapping>> ResolveMappingsAsync(IEnumerable<string> hashedIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ResourceIdMapping>>(Array.Empty<ResourceIdMapping>());
}
