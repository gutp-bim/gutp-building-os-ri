namespace BuildingOS.Shared.Domain.Authorization;

public interface IResourceHierarchyResolver
{
    /// <summary>
    /// 指定リソースの祖先チェーンを取得（Building→Floor→Space→Device→Point階層）
    /// </summary>
    Task<IReadOnlyList<(string ResourceType, string ResourceId)>> GetAncestorsAsync(
        string resourceType, string resourceId, CancellationToken ct = default);
}
