namespace BuildingOS.Shared.Domain.Authorization;

public interface IResourceHierarchyResolver
{
    /// <summary>
    /// 指定リソースの祖先チェーンを取得（Building→Floor→Space→Device→Point階層）
    /// </summary>
    Task<IReadOnlyList<(string ResourceType, string ResourceId)>> GetAncestorsAsync(
        string resourceType, string resourceId, CancellationToken ct = default);

    /// <summary>
    /// 同じ種別の複数リソースについて、祖先の和集合を取得する（#548）。どの祖先がどのリソースの
    /// ものかは返さない — 「辿るための入れ物として見せてよいノード」の集合を作るための読み取り。
    /// 既定実装は 1 件ずつ <see cref="GetAncestorsAsync"/> を呼ぶ。実装側はまとめて引いてよい。
    /// </summary>
    async Task<IReadOnlyCollection<(string ResourceType, string ResourceId)>> GetAncestorUnionAsync(
        string resourceType, IReadOnlyCollection<string> resourceIds, CancellationToken ct = default)
    {
        var union = new HashSet<(string, string)>();
        foreach (var id in resourceIds)
            foreach (var ancestor in await GetAncestorsAsync(resourceType, id, ct).ConfigureAwait(false))
                union.Add(ancestor);
        return union;
    }
}
