using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Domain.Authorization;

public class DefaultAuthorizationService : IAuthorizationService
{
    private readonly IGroupMembershipResolver _groupResolver;
    private readonly IResourceHierarchyResolver _hierarchyResolver;
    private readonly ILogger<DefaultAuthorizationService> _logger;

    public DefaultAuthorizationService(
        IGroupMembershipResolver groupResolver,
        IResourceHierarchyResolver hierarchyResolver,
        ILogger<DefaultAuthorizationService> logger)
    {
        _groupResolver = groupResolver;
        _hierarchyResolver = hierarchyResolver;
        _logger = logger;
    }

    public async Task<bool> CanAccessAsync(
        AuthorizationContext context,
        string resourceType,
        string resourceId,
        string action,
        CancellationToken cancellationToken = default)
    {
        // 1. Admin → 即許可
        if (context.IsAdmin)
        {
            return true;
        }

        // 2. 直接権限チェック
        if (HasDirectPermission(context.Permissions, resourceType, resourceId, action))
        {
            return true;
        }

        // 3. 祖先チェーン解決
        var ancestors = await _hierarchyResolver.GetAncestorsAsync(
            resourceType, resourceId, cancellationToken).ConfigureAwait(false);

        // 4. 各祖先の直接権限チェック
        foreach (var (ancestorType, ancestorId) in ancestors)
        {
            if (HasDirectPermission(context.Permissions, ancestorType, ancestorId, action))
            {
                return true;
            }
        }

        // 5. グループ権限チェック（対象リソース + 各祖先）
        var resourcesToCheck = new List<(string ResourceType, string ResourceId)>
        {
            (resourceType, resourceId)
        };
        resourcesToCheck.AddRange(ancestors);

        foreach (var (resType, resId) in resourcesToCheck)
        {
            var groupIds = await _groupResolver.GetGroupsContainingResourceAsync(
                resType, resId, cancellationToken).ConfigureAwait(false);

            foreach (var groupId in groupIds)
            {
                if (HasDirectPermission(context.Permissions, "group", groupId, action))
                {
                    return true;
                }
            }
        }

        // 6. 拒否
        _logger.LogDebug(
            "Access denied: User {UserId} attempted {Action} on {ResourceType}:{ResourceId}",
            context.UserId, action, resourceType, resourceId);

        return false;
    }

    public async Task<IReadOnlyList<string>> GetAccessibleResourceIdsAsync(
        AuthorizationContext context,
        string resourceType,
        string action,
        CancellationToken cancellationToken = default)
    {
        if (context.IsAdmin)
        {
            // admin は全リソースアクセス可能（呼び出し元で全件取得の意味）
            return Array.Empty<string>();
        }

        var result = new HashSet<string>();

        foreach (var permission in context.Permissions)
        {
            var parsed = PermissionHelper.ParsePermissionString(permission);
            if (parsed == null) continue;

            var (type, id, actionsStr) = parsed.Value;
            if (!HasMatchingAction(actionsStr, action)) continue;

            if (type == resourceType)
            {
                result.Add(id);
            }
            else if (type == "group")
            {
                // グループ権限: 同タイプのメンバーを展開
                var members = await _groupResolver.GetGroupMembersAsync(
                    id, resourceType, cancellationToken).ConfigureAwait(false);
                foreach (var member in members)
                {
                    result.Add(PermissionHelper.HashResourceId(member));
                }
            }
        }

        return result.ToList();
    }

    private static bool HasMatchingAction(string actionsStr, string action)
    {
        var actions = actionsStr.Split(',');
        if (actions.Contains(action) || actions.Contains("admin")) return true;

        // write権限があればreadも許可（書き込めるのに読めないのは不自然）
        if (action == "read" && actions.Contains("write")) return true;

        return false;
    }

    private static bool HasDirectPermission(
        IReadOnlyList<string> permissions,
        string resourceType,
        string resourceId,
        string requiredAction)
    {
        foreach (var permission in permissions)
        {
            var parsed = PermissionHelper.ParsePermissionString(permission);
            if (parsed == null) continue;

            var (type, id, actionsStr) = parsed.Value;

            if (type != resourceType) continue;

            // グループIDはハッシュ化されていない（生IDのまま比較）
            // リソースIDはハッシュ化されているため、入力IDもハッシュ化して比較
            var idToCompare = PermissionHelper.IsGroupType(resourceType)
                ? resourceId
                : PermissionHelper.HashResourceId(resourceId);
            if (id != idToCompare) continue;

            var actions = actionsStr.Split(',');

            // admin アクションは全アクションを含む
            if (actions.Contains("admin"))
            {
                return true;
            }

            if (actions.Contains(requiredAction))
            {
                return true;
            }

            // write権限があればreadも許可
            if (requiredAction == "read" && actions.Contains("write"))
            {
                return true;
            }
        }

        return false;
    }
}
