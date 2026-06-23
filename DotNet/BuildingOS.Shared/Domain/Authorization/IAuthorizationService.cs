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
}
