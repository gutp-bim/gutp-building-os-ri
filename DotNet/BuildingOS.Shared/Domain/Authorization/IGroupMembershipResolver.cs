namespace BuildingOS.Shared.Domain.Authorization;

public interface IGroupMembershipResolver
{
    /// <summary>
    /// 指定リソースが属するグループIDリストを取得
    /// </summary>
    Task<IReadOnlyList<string>> GetGroupsContainingResourceAsync(
        string resourceType,
        string resourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定グループに属するリソースIDリストを取得
    /// </summary>
    Task<IReadOnlyList<string>> GetGroupMembersAsync(
        string groupId,
        string resourceType,
        CancellationToken cancellationToken = default);
}
