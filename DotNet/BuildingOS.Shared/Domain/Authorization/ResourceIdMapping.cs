namespace BuildingOS.Shared.Domain.Authorization;

/// <summary>
/// パーミッション文字列のハッシュ化されたリソースIDと元のリソースIDのマッピング。
/// Entra IDの64文字制限のためリソースIDをハッシュ化して保存するが、
/// MyResources取得時に元のIDに逆引きするために使用する。
/// </summary>
public class ResourceIdMapping
{
    /// <summary>
    /// ハッシュ化されたリソースID（SHA-256先頭224bit = 56hex）= PK
    /// </summary>
    public string HashedId { get; set; } = default!;

    /// <summary>
    /// リソースの種別（building, floor, space, device, point）
    /// </summary>
    public string ResourceType { get; set; } = default!;

    /// <summary>
    /// 元のリソースID（ADT dtIdやビジネスID）
    /// </summary>
    public string OriginalId { get; set; } = default!;

    /// <summary>
    /// リソースの表示名（パーミッション付与時にフロントエンドから受け取る）
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// 作成日時
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
