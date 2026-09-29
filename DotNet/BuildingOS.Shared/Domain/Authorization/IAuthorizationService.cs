namespace BuildingOS.Shared.Domain.Authorization;

public interface IAuthorizationService
{
    /// <summary>
    /// 指定リソースへのアクセス可否を判定
    /// </summary>
    Task<bool> CanAccessAsync(
        AuthorizationContext context,
        string resourceType,
        string resourceId,
        string action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// アクセス可能なリソースIDリストを取得
    /// </summary>
    Task<IReadOnlyList<string>> GetAccessibleResourceIdsAsync(
        AuthorizationContext context,
        string resourceType,
        string action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="GetAccessibleResourceIdsAsync"/> with each resource's original (business) id when it
    /// is known here — a Group member's id is, a direct permission's is not (it is stored hashed) — so a
    /// caller can return usable ids instead of hashes (#504 B). The default keeps the hashes only.
    /// </summary>
    async Task<IReadOnlyList<AccessibleResource>> GetAccessibleResourcesAsync(
        AuthorizationContext context,
        string resourceType,
        string action,
        CancellationToken cancellationToken = default)
        => (await GetAccessibleResourceIdsAsync(context, resourceType, action, cancellationToken).ConfigureAwait(false))
            .Select(hash => new AccessibleResource(hash, null))
            .ToList();
}

/// <summary>An accessible resource: the hashed id authorization compares, and its original id when known.</summary>
public sealed record AccessibleResource(string Hash, string? OriginalId);
