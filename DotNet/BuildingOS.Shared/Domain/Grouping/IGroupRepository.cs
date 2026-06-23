namespace BuildingOS.Shared.Domain.Grouping;

using BuildingOS.Shared.Domain.Grouping.Entities;

/// <summary>
/// リソースグループのリポジトリインターフェース
/// </summary>
public interface IGroupRepository
{
    // === Group CRUD ===

    /// <summary>
    /// IDでグループを取得（リソースアイテムは含まない）
    /// </summary>
    Task<ResourceGroup?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// IDでグループを取得（リソースアイテムを含む）
    /// </summary>
    Task<ResourceGroup?> GetByIdWithItemsAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// 全グループを取得
    /// </summary>
    Task<IReadOnlyList<ResourceGroup>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// グループを作成
    /// </summary>
    Task<ResourceGroup> CreateAsync(ResourceGroup group, CancellationToken ct = default);

    /// <summary>
    /// グループを更新
    /// </summary>
    Task UpdateAsync(ResourceGroup group, CancellationToken ct = default);

    /// <summary>
    /// グループを削除
    /// </summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    // === ResourceItem ===

    /// <summary>
    /// グループにリソースアイテムを追加
    /// </summary>
    Task<GroupResourceItem> AddResourceItemAsync(
        string groupId,
        string resourceType,
        string resourceId,
        CancellationToken ct = default);

    /// <summary>
    /// リソースアイテムを削除
    /// </summary>
    Task RemoveResourceItemAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// グループのリソースアイテム一覧を取得
    /// </summary>
    Task<IReadOnlyList<GroupResourceItem>> GetResourceItemsAsync(string groupId, CancellationToken ct = default);

    // === 逆引き ===

    /// <summary>
    /// 指定リソースが属するグループIDリストを取得
    /// </summary>
    Task<IReadOnlyList<string>> GetGroupIdsForResourceAsync(
        string resourceType,
        string resourceId,
        CancellationToken ct = default);

    /// <summary>
    /// 指定グループに属するリソースIDリストを取得
    /// </summary>
    Task<IReadOnlyList<string>> GetResourceIdsInGroupAsync(
        string groupId,
        string resourceType,
        CancellationToken ct = default);
}
