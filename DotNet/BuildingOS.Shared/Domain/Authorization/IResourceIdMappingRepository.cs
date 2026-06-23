namespace BuildingOS.Shared.Domain.Authorization;

/// <summary>
/// ハッシュ化されたリソースID → 元IDのマッピングリポジトリ
/// </summary>
public interface IResourceIdMappingRepository
{
    /// <summary>
    /// マッピングを保存（既に存在する場合はDisplayNameのみ更新）
    /// </summary>
    Task SaveMappingAsync(string resourceType, string originalId, string? displayName = null, CancellationToken ct = default);

    /// <summary>
    /// 複数のハッシュIDから元IDを一括取得
    /// </summary>
    /// <returns>ハッシュID → 元IDのディクショナリ</returns>
    Task<IReadOnlyDictionary<string, string>> ResolveOriginalIdsAsync(
        IEnumerable<string> hashedIds, CancellationToken ct = default);

    /// <summary>
    /// 複数のハッシュIDからマッピング情報（元ID・リソースタイプ・表示名）を一括取得
    /// </summary>
    Task<IReadOnlyList<ResourceIdMapping>> ResolveMappingsAsync(
        IEnumerable<string> hashedIds, CancellationToken ct = default);
}
