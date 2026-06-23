namespace BuildingOs.ApiServer.Controllers;

using System.Text.Json;
using BuildingOs.ApiServer.Extensions;
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
[ProducesResponseType(StatusCodes.Status403Forbidden)]
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
            // ハッシュ→元IDのマッピングを保存（逆引き用）
            if (request.Permissions != null)
            {
                foreach (var permission in request.Permissions)
                {
                    await SavePermissionMappingAsync(permission, request.ResourceDisplayNames, ct).ConfigureAwait(false);
                }
            }

            var updateRequest = new UpdateUserAttributesRequest
            {
                Role = request.Role,
                Permissions = request.Permissions?.Select(HashPermissionResourceId).ToList()
            };

            var user = await _userService.UpdateUserAttributesAsync(id, updateRequest, ct).ConfigureAwait(false);
            await AuditAsync(authContext, "set-attributes", id, AdminAuditResult.Success,
                new { role = request.Role, permissions = request.Permissions?.Count ?? 0 }, ct).ConfigureAwait(false);
            return Ok(ToResponse(user));
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

        // ハッシュ→元IDのマッピングを保存（逆引き用）
        await SavePermissionMappingAsync(request.Permission, null, ct).ConfigureAwait(false);

        var updateRequest = new UpdateUserAttributesRequest
        {
            Permissions = permissions
        };

        var updated = await _userService.UpdateUserAttributesAsync(id, updateRequest, ct).ConfigureAwait(false);
        await AuditAsync(authContext, "add-permission", id, AdminAuditResult.Success,
            new { permission = request.Permission }, ct).ConfigureAwait(false);
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
