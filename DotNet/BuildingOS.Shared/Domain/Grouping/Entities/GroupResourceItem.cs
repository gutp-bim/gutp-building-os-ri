namespace BuildingOS.Shared.Domain.Grouping.Entities;

/// <summary>
/// グループに含まれるリソースアイテム
/// ResourceGroupとリソース（Building, Floor, Space, Device, Point）の関連を表す
/// </summary>
public class GroupResourceItem
{
    /// <summary>
    /// アイテムID（ULID等の一意識別子）
    /// </summary>
    public string Id { get; set; } = default!;

    /// <summary>
    /// 所属するグループのID
    /// </summary>
    public string GroupId { get; set; } = default!;

    /// <summary>
    /// リソースの種別（building, floor, space, device, point）
    /// </summary>
    public string ResourceType { get; set; } = default!;

    /// <summary>
    /// リソースのID（Digital TwinsのdtId等）
    /// </summary>
    public string ResourceId { get; set; } = default!;

    /// <summary>
    /// 作成日時
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 所属するグループ（ナビゲーションプロパティ）
    /// </summary>
    public ResourceGroup Group { get; set; } = default!;
}
