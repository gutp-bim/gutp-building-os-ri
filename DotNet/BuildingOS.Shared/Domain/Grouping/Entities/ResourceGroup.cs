namespace BuildingOS.Shared.Domain.Grouping.Entities;

/// <summary>
/// リソースグループエンティティ
/// 任意のリソース（Building, Floor, Space, Device, Point）をグループ化するための単位
/// </summary>
public class ResourceGroup
{
    /// <summary>
    /// グループID（例: hvac-team, tenant-3f）
    /// </summary>
    public string Id { get; set; } = default!;

    /// <summary>
    /// グループ表示名
    /// </summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// グループの説明
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 作成日時
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 更新日時
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// このグループに含まれるリソースアイテム
    /// </summary>
    public ICollection<GroupResourceItem> ResourceItems { get; set; } = [];
}
