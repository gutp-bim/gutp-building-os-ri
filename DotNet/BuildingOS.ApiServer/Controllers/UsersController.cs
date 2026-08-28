namespace BuildingOs.ApiServer.Controllers;

using System.Text.Json;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.UserManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Azure Entra ID ユーザー管理API（admin専用）
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[UserManagementUnavailableFilter]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
public class UsersController : ControllerBase
{
    private readonly IUserManagementService _userService;
    private readonly IResourceIdMappingRepository _mappingRepository;
    private readonly IAdminAuditRecorder _audit;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        IUserManagementService userService,
        IResourceIdMappingRepository mappingRepository,
        IAdminAuditRecorder audit,
        ILogger<UsersController> logger)
    {
        _userService = userService;
        _mappingRepository = mappingRepository;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// ユーザー一覧を取得
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<UserResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<UserResponse>>> GetAll(CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var users = await _userService.GetUsersAsync(ct).ConfigureAwait(false);
        return Ok(users.Select(ToResponse));
    }

    /// <summary>
    /// ユーザー詳細を取得
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> GetById(string id, CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var user = await _userService.GetUserByIdAsync(id, ct).ConfigureAwait(false);
        if (user == null)
        {
            return NotFound();
        }
        return Ok(ToResponse(user));
    }

    /// <summary>
    /// ユーザーのBuilding OS属性を更新
    /// </summary>
    [HttpPatch("{id}/attributes")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserResponse>> UpdateAttributes(
        string id,
        [FromBody] UpdateUserAttributesApiRequest request,
        CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        // Reject role changes that would lock the actor out or remove the last admin (#325).
        if (request.Role != null)
        {
            var users = await _userService.GetUsersAsync(ct).ConfigureAwait(false);
            var guard = UserAdminGuard.CheckSetRole(
                authContext.UserId, id, request.Role, ToRoleStates(users));
            if (guard != UserAdminGuardResult.Allowed)
            {
                await AuditAsync(authContext, "set-role", id, AdminAuditResult.Failure,
                    new { role = request.Role, blocked = guard.ToString() }, ct).ConfigureAwait(false);
                return Conflict(new { error = LockoutMessage(guard) });
            }
        }

        try
        {
            var updateRequest = new UpdateUserAttributesRequest
            {
                Role = request.Role,
                Permissions = request.Permissions?.Select(HashPermissionResourceId).ToList()
            };

            var user = await _userService.UpdateUserAttributesAsync(id, updateRequest, ct).ConfigureAwait(false);

            // ハッシュ→元IDのマッピングを保存（逆引き用）。
            // The update has to land first. Saving these before it meant a request that ended in 503
            // (Keycloak unconfigured) or any other failure still persisted the resource-id mappings —
            // a write committed for an operation the caller was told did not happen. The mapping is a
            // reverse-lookup record for permissions that now exist, so it is only true once they do.
            //
            // And because it lands second, its failure must not be reported as the update failing:
            // the attributes are already committed in Keycloak (#307).
            var mappingError = await TrySavePermissionMappingsAsync(
                authContext, "set-attributes", id, request.Permissions, request.ResourceDisplayNames, ct)
                .ConfigureAwait(false);

            await AuditAsync(authContext, "set-attributes", id, AdminAuditResult.Success,
                new
                {
                    role = request.Role,
                    permissions = request.Permissions?.Count ?? 0,
                    resourceIdMappingSaved = mappingError is null
                }, ct).ConfigureAwait(false);
            return Ok(ToResponse(user));
        }
        catch (UserManagementUnavailableException)
        {
            // Not a bad request — Keycloak admin is unconfigured. Rethrow past the catch-all below so
            // UserManagementUnavailableFilter can answer 503 instead of reporting a deployment gap as
            // a client error (#293). The filter also writes the failure audit, which is why there is
            // none here: it covers the paths that throw before this try block too (#303).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update attributes for user {UserId}", id);
            await AuditAsync(authContext, "set-attributes", id, AdminAuditResult.Failure,
                new { error = ex.Message }, ct).ConfigureAwait(false);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// 割当可能なロール（admin / operator / viewer）のカタログを取得する。各ロールが見えるワークスペースと
    /// admin 権限の有無を含む（読み取り専用 SSOT）。管理者のみ。
    /// </summary>
    [HttpGet("roles")]
    [ProducesResponseType(typeof(IReadOnlyList<RoleCatalogEntry>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<RoleCatalogEntry>> GetRoles()
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();
        return Ok(RoleCatalog.Entries);
    }

    /// <summary>
    /// ユーザーを有効化／無効化する（Keycloak <c>enabled</c>）。削除はせず、認証だけを止める（可逆）。
    /// 自己無効化・最後の admin 無効化はロックアウト防止のため 409。管理者のみ。
    /// </summary>
    [HttpPut("{id}/enabled")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> SetEnabled(
        string id,
        [FromBody] SetEnabledRequest request,
        CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var users = await _userService.GetUsersAsync(ct).ConfigureAwait(false);
        if (users.All(u => u.Id != id))
        {
            return NotFound();
        }

        var guard = UserAdminGuard.CheckSetEnabled(
            authContext.UserId, id, request.Enabled, ToRoleStates(users));
        if (guard != UserAdminGuardResult.Allowed)
        {
            await AuditAsync(authContext, "set-enabled", id, AdminAuditResult.Failure,
                new { enabled = request.Enabled, blocked = guard.ToString() }, ct).ConfigureAwait(false);
            return Conflict(new { error = LockoutMessage(guard) });
        }

        try
        {
            var updated = await _userService.SetEnabledAsync(id, request.Enabled, ct).ConfigureAwait(false);
            await AuditAsync(authContext, "set-enabled", id, AdminAuditResult.Success,
                new { enabled = request.Enabled }, ct).ConfigureAwait(false);
            return Ok(ToResponse(updated));
        }
        catch (UserManagementUnavailableException)
        {
            // See UpdateAttributes: 503 not 400, and the filter owns the audit (#293, #303).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set enabled={Enabled} for user {UserId}", request.Enabled, id);
            await AuditAsync(authContext, "set-enabled", id, AdminAuditResult.Failure,
                new { error = ex.Message }, ct).ConfigureAwait(false);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// ユーザーにパーミッションを追加
    /// </summary>
    [HttpPost("{id}/permissions")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> AddPermission(
        string id,
        [FromBody] AddPermissionRequest request,
        CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var user = await _userService.GetUserByIdAsync(id, ct).ConfigureAwait(false);
        if (user == null)
        {
            return NotFound();
        }

        // Add the new permission to existing permissions (resourceIdをハッシュ化して保存)
        var hashedPermission = HashPermissionResourceId(request.Permission);
        var permissions = user.Permissions.ToList();
        if (!permissions.Contains(hashedPermission))
        {
            permissions.Add(hashedPermission);
        }

        var updateRequest = new UpdateUserAttributesRequest
        {
            Permissions = permissions
        };

        var updated = await _userService.UpdateUserAttributesAsync(id, updateRequest, ct).ConfigureAwait(false);

        // ハッシュ→元IDのマッピングを保存（逆引き用）。Saved after the grant lands, for the reason
        // UpdateAttributes spells out: writing it first leaves a mapping behind for a permission the
        // caller was told was never granted. Its own failure does not undo the grant (#307).
        var mappingError = await TrySavePermissionMappingsAsync(
            authContext, "add-permission", id, new[] { request.Permission }, null, ct).ConfigureAwait(false);

        await AuditAsync(authContext, "add-permission", id, AdminAuditResult.Success,
            new { permission = request.Permission, resourceIdMappingSaved = mappingError is null }, ct)
            .ConfigureAwait(false);
        return Ok(ToResponse(updated));
    }

    /// <summary>
    /// ユーザーからパーミッションを削除
    /// </summary>
    [HttpDelete("{id}/permissions")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> RemovePermission(
        string id,
        [FromBody] RemovePermissionRequest request,
        CancellationToken ct)
    {
        var authContext = HttpContext.GetAuthorizationContext();
        if (!authContext.IsAdmin) return Forbid();

        var user = await _userService.GetUserByIdAsync(id, ct).ConfigureAwait(false);
        if (user == null)
        {
            return NotFound();
        }

        // Remove the permission from existing permissions (resourceIdをハッシュ化して比較)
        var hashedPermission = HashPermissionResourceId(request.Permission);
        var permissions = user.Permissions.Where(p => p != hashedPermission).ToList();

        var updateRequest = new UpdateUserAttributesRequest
        {
            Permissions = permissions
        };

        var updated = await _userService.UpdateUserAttributesAsync(id, updateRequest, ct).ConfigureAwait(false);
        await AuditAsync(authContext, "remove-permission", id, AdminAuditResult.Success,
            new { permission = request.Permission }, ct).ConfigureAwait(false);
        return Ok(ToResponse(updated));
    }

    // === Helpers ===

    private Task AuditAsync(
        AuthorizationContext auth, string action, string targetId,
        AdminAuditResult result, object? detail, CancellationToken ct)
    {
        var detailJson = detail is null ? null : JsonSerializer.Serialize(detail);
        var record = AdminAuditRecord.Create(
            AdminAuditSubjects.User, action, targetId, auth.UserId, actorName: null, result, detailJson);
        return _audit.RecordAsync(record, ct);
    }

    private static IReadOnlyList<UserRoleState> ToRoleStates(IReadOnlyList<EntraUser> users) =>
        users.Select(u => new UserRoleState(u.Id, u.Role, u.Enabled)).ToList();

    private static string LockoutMessage(UserAdminGuardResult guard) => guard switch
    {
        UserAdminGuardResult.SelfLockout => "自分自身を無効化／降格することはできません（ロックアウト防止）。",
        UserAdminGuardResult.LastAdmin => "最後の有効な管理者を無効化／降格することはできません（ロックアウト防止）。",
        _ => "操作はロックアウト防止のため拒否されました。",
    };

    /// <summary>
    /// パーミッション文字列内のリソースIDをハッシュ化し、省略形式に変換する。
    /// グループタイプのパーミッションはハッシュ化しない。
    /// 不正なフォーマットのパーミッションはそのまま返す。
    /// </summary>
    private static string HashPermissionResourceId(string permission)
    {
        var parsed = PermissionHelper.ParsePermissionString(permission);
        if (parsed == null) return permission;
        var (resourceType, resourceId, actions) = parsed.Value;
        return PermissionHelper.BuildPermissionString(resourceType, resourceId, actions);
    }

    /// <summary>
    /// パーミッション文字列からリソースIDのハッシュ→元IDマッピングを保存する。
    /// グループタイプのパーミッションはハッシュ化しないため保存不要。
    /// </summary>
    /// <summary>
    /// Persists the hash→original-id mappings for permissions that have just been granted, and
    /// reports a failure instead of raising it (#307).
    ///
    /// The caller has already committed the grant in Keycloak by the time this runs. Letting a
    /// failure here surface as the request's failure told the client the update had not happened
    /// while the remote side had already changed, which invites a retry of something that is
    /// already done.
    ///
    /// **What is actually lost when this fails.** The mapping is a reverse-lookup record only:
    /// authorization compares hashes, so the permission itself keeps working. What degrades is
    /// resolving a hash back to the resource it names — <c>MyResourcesController</c>'s
    /// <c>ResolveOriginalIdsAsync</c>, which is how a user's accessible-resource list is built.
    /// A permission whose mapping is missing therefore grants access but does not show up in that
    /// list. That is a discoverability gap, not a hole: it fails closed.
    ///
    /// **How it is repaired.** Re-issuing the same request. Both call sites write the mapping
    /// unconditionally for every permission in the request, and <c>SaveMappingAsync</c> is an
    /// upsert, so a later successful PATCH of the same permission set restores what this one
    /// missed. That is why there is no outbox or reconciliation job here: the operation that
    /// creates the record is idempotent and operators already repeat it. The failure is recorded
    /// in the audit log (action + <c>resource-id-mapping</c>, result Failure) so the gap is
    /// findable rather than silent, and the success audit carries
    /// <c>resourceIdMappingSaved</c> so a partial outcome is visible on the successful path too.
    /// </summary>
    /// <returns>The failure message, or <c>null</c> when every mapping was saved.</returns>
    private async Task<string?> TrySavePermissionMappingsAsync(
        AuthorizationContext auth,
        string action,
        string targetId,
        IEnumerable<string>? permissions,
        Dictionary<string, string>? displayNames,
        CancellationToken ct)
    {
        if (permissions is null) return null;

        try
        {
            foreach (var permission in permissions)
            {
                await SavePermissionMappingAsync(permission, displayNames, ct).ConfigureAwait(false);
            }
            return null;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: see the summary. The grant stands; only the reverse lookup
            // is incomplete.
            _logger.LogWarning(ex,
                "Resource-id mapping not saved for user {UserId} after {Action} succeeded; " +
                "the permission is in effect but will not appear in accessible-resource listings " +
                "until the request is repeated", targetId, action);

            await AuditAsync(auth, $"{action}:resource-id-mapping", targetId, AdminAuditResult.Failure,
                new { error = ex.Message }, ct).ConfigureAwait(false);

            return ex.Message;
        }
    }

    private async Task SavePermissionMappingAsync(string permission, Dictionary<string, string>? displayNames, CancellationToken ct)
    {
        var parsed = PermissionHelper.ParsePermissionString(permission);
        if (parsed == null) return;

        var (resourceType, resourceId, _) = parsed.Value;
        if (PermissionHelper.IsGroupType(resourceType)) return;
        if (PermissionHelper.IsAlreadyHashed(resourceId)) return;

        string? displayName = null;
        displayNames?.TryGetValue(resourceId, out displayName);
        await _mappingRepository.SaveMappingAsync(resourceType, resourceId, displayName, ct).ConfigureAwait(false);
    }

    // === Response/Request DTOs ===

    private static UserResponse ToResponse(EntraUser user) => new()
    {
        Id = user.Id,
        DisplayName = user.DisplayName,
        Email = user.Email,
        UserPrincipalName = user.UserPrincipalName,
        Role = user.Role,
        Permissions = user.Permissions.ToList(),
        Enabled = user.Enabled
    };

    // === Response Models ===

    public record UserResponse
    {
        public string Id { get; init; } = default!;
        public string DisplayName { get; init; } = default!;
        public string? Email { get; init; }
        public string? UserPrincipalName { get; init; }
        public string? Role { get; init; }
        public List<string> Permissions { get; init; } = [];
        public bool Enabled { get; init; } = true;
    }

    // === Request Models ===

    public record SetEnabledRequest
    {
        public bool Enabled { get; init; }
    }

    public record UpdateUserAttributesApiRequest
    {
        public string? Role { get; init; }
        public List<string>? Permissions { get; init; }
        /// <summary>
        /// リソースIDに対応する表示名のマップ（キー: 元のリソースID、値: 表示名）
        /// </summary>
        public Dictionary<string, string>? ResourceDisplayNames { get; init; }
    }

    public record AddPermissionRequest
    {
        public string Permission { get; init; } = default!;
    }

    public record RemovePermissionRequest
    {
        public string Permission { get; init; } = default!;
    }
}
