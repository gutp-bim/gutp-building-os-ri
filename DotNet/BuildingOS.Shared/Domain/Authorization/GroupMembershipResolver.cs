using BuildingOS.Shared.Domain.Grouping;

namespace BuildingOS.Shared.Domain.Authorization;

public class GroupMembershipResolver : IGroupMembershipResolver
{
    private readonly IGroupRepository _groupRepository;

    public GroupMembershipResolver(IGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
    }

    public async Task<IReadOnlyList<string>> GetGroupsContainingResourceAsync(
        string resourceType,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        return await _groupRepository.GetGroupIdsForResourceAsync(
            resourceType, resourceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetGroupMembersAsync(
        string groupId,
        string resourceType,
        CancellationToken cancellationToken = default)
    {
        return await _groupRepository.GetResourceIdsInGroupAsync(
            groupId, resourceType, cancellationToken).ConfigureAwait(false);
    }
}
