using BuildingOs.ApiServer.Routing;
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
[Route(ApiRoutes.V1 + "/users")]
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

        // Trim the role, map blank to "clear", and reject roles outside RoleCatalog before anything
        // else sees it: whitespace or an unknown string would otherwise become the building_os_role claim.
        // This is the only place a role write is validated; the service takes the normalized value.
        string? role;
        try
        {
            role = KeycloakUserAttributes.NormalizeRole(request.Role);
        }
        catch (ArgumentException ex)
        {
            await AuditAsync(authContext, "set-attributes", id, AdminAuditResult.Failure,
                new { error = ex.Message }, ct).ConfigureAwait(false);
            return BadRequest(new { error = ex.Message });
        }

        try
        {
            // Reject role changes that would lock the actor out or remove the last admin (#325). The guard
            // sees the effective role, including an admin inherited from a Keycloak group (#519 follow-up),
            // and resolves only what it needs — nothing for a promotion or a permission-only write. Its
            // Keycloak lookups run inside this try so a failure is audited like the write's own.
            if (UserAdminGuard.SetRoleNeedsGuard(role))
            {
                var guard = await CheckGuardAsync(
                    authContext, id, UserAdminGuard.SetRoleNeedsTargetGroupRole(role),
                    target => UserAdminGuard.PreCheckSetRole(authContext.UserId, target, role), ct)
                    .ConfigureAwait(false);
                if (guard != UserAdminGuardResult.Allowed)
                {
                    await AuditAsync(authContext, "set-role", id, AdminAuditResult.Failure,
                        new { role, blocked = guard.ToString() }, ct).ConfigureAwait(false);
                    return Conflict(new { error = LockoutMessage(guard) });
                }
            }

            var updateRequest = new UpdateUserAttributesRequest
            {
                Role = role,
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
                    role,
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
        catch (UserNotFoundException)
        {
            await AuditAsync(authContext, "set-attributes", id, AdminAuditResult.Failure,
                new { error = "not found" }, ct).ConfigureAwait(false);
            return NotFound();
        }
        catch (UserAttributesNotPersistedException ex)
        {
            return await NotPersistedAsync(authContext, "set-attributes", id, ex, ct).ConfigureAwait(false);
        }
        catch (UserAttributesWrittenUnverifiedException ex)
        {
            // Keycloak accepted the PUT; only the verifying read failed (#532). Answer it as written, save the
            // reverse lookup for the permissions it granted, and leave a warning on the success audit.
            LogUnverified(ex, "set-attributes", id);
            var mappingError = await TrySavePermissionMappingsAsync(
                authContext, "set-attributes", id, request.Permissions, request.ResourceDisplayNames,
                CancellationToken.None).ConfigureAwait(false);
            await AuditAsync(authContext, "set-attributes", id, AdminAuditResult.Success,
                new
                {
                    role,
                    permissions = request.Permissions?.Count ?? 0,
                    resourceIdMappingSaved = mappingError is null,
                    verified = false,
                    warning = UnverifiedWarning(ex),
                }, CancellationToken.None).ConfigureAwait(false);
            return Ok(ToResponse(ex.Written));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update attributes for user {UserId}", ForLog(id));
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

        try
        {
            // Effective roles, so an admin inherited from a Keycloak group counts (#519 follow-up). Enabling
            // needs no check; disabling a non-admin needs only the target, not every user.
            if (UserAdminGuard.SetEnabledNeedsGuard(request.Enabled))
            {
                var guard = await CheckGuardAsync(
                    authContext, id, includeTargetGroupRole: false,
                    target => UserAdminGuard.PreCheckSetEnabled(authContext.UserId, target, request.Enabled), ct)
                    .ConfigureAwait(false);
                if (guard != UserAdminGuardResult.Allowed)
                {
                    await AuditAsync(authContext, "set-enabled", id, AdminAuditResult.Failure,
                        new { enabled = request.Enabled, blocked = guard.ToString() }, ct).ConfigureAwait(false);
                    return Conflict(new { error = LockoutMessage(guard) });
                }
            }

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
        catch (UserNotFoundException)
        {
            await AuditAsync(authContext, "set-enabled", id, AdminAuditResult.Failure,
                new { enabled = request.Enabled, error = "not found" }, ct).ConfigureAwait(false);
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set enabled={Enabled} for user {UserId}", request.Enabled, ForLog(id));
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

        // Add the new permission to existing permissions (resourceIdをハッシュ化して保存). The service
        // applies it to the set it reads for the write, so there is no separate read here.
        var updateRequest = new UpdateUserAttributesRequest
        {
            PermissionsToAdd = [HashPermissionResourceId(request.Permission)]
        };

        var write = await TryWritePermissionAsync(
            authContext, "add-permission", id, request.Permission, updateRequest, ct).ConfigureAwait(false);
        if (write.Failure is not null) return write.Failure;

        // ハッシュ→元IDのマッピングを保存（逆引き用）。Saved after the grant lands, for the reason
        // UpdateAttributes spells out: writing it first leaves a mapping behind for a permission the
        // caller was told was never granted. Its own failure does not undo the grant (#307). "Lands" is
        // Keycloak accepting the PUT — also when only the verifying read failed afterwards (#532).
        var writeCt = write.Unverified is null ? ct : CancellationToken.None;
        var mappingError = await TrySavePermissionMappingsAsync(
            authContext, "add-permission", id, new[] { request.Permission }, null, writeCt).ConfigureAwait(false);

        await AuditAsync(authContext, "add-permission", id, AdminAuditResult.Success,
            write.Unverified is null
                ? new { permission = request.Permission, resourceIdMappingSaved = mappingError is null }
                : new
                {
                    permission = request.Permission,
                    resourceIdMappingSaved = mappingError is null,
                    verified = false,
                    warning = UnverifiedWarning(write.Unverified),
                },
            writeCt).ConfigureAwait(false);
        return Ok(ToResponse(write.Updated!));
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

        // Remove the permission from existing permissions (resourceIdをハッシュ化して比較), in the
        // service's single read-modify-write.
        var updateRequest = new UpdateUserAttributesRequest
        {
            PermissionsToRemove = [HashPermissionResourceId(request.Permission)]
        };

        var write = await TryWritePermissionAsync(
            authContext, "remove-permission", id, request.Permission, updateRequest, ct).ConfigureAwait(false);
        if (write.Failure is not null) return write.Failure;

        if (write.Unverified is null)
        {
            await AuditAsync(authContext, "remove-permission", id, AdminAuditResult.Success,
                new { permission = request.Permission }, ct).ConfigureAwait(false);
        }
        else
        {
            await AuditAsync(authContext, "remove-permission", id, AdminAuditResult.Success,
                new { permission = request.Permission, verified = false, warning = UnverifiedWarning(write.Unverified) },
                CancellationToken.None).ConfigureAwait(false);
        }
        return Ok(ToResponse(write.Updated!));
    }

    // === Helpers ===

    /// <summary>
    /// Runs the lockout guard, resolving only what it needs: the target's role state, and every user's
    /// only when <paramref name="preCheck"/> cannot decide from the target alone.
    /// </summary>
    /// <exception cref="UserNotFoundException">The target does not exist.</exception>
    private async Task<UserAdminGuardResult> CheckGuardAsync(
        AuthorizationContext auth, string targetId, bool includeTargetGroupRole,
        Func<UserRoleState, UserAdminGuardResult?> preCheck, CancellationToken ct)
    {
        // One lookup session: the target and (only if needed) the snapshot share a token and group cache.
        var lookup = await _userService.CreateRoleLookupAsync(ct).ConfigureAwait(false);
        var target = await lookup.GetUserAsync(targetId, includeTargetGroupRole, ct).ConfigureAwait(false)
                     ?? throw new UserNotFoundException(targetId);
        if (preCheck(target) is { } decided) return decided;

        var all = await lookup.GetAllAsync(ct).ConfigureAwait(false);
        // The actor is an admin per their token (checked by every action), so they count as remaining.
        return UserAdminGuard.CheckLastAdmin(targetId, all, auth.UserId);
    }

    /// <summary>
    /// Keycloak answered the write but did not store it (e.g. no <c>unmanagedAttributePolicy</c>): an
    /// upstream failure, not a success and not the caller's fault — 502 with a failure audit.
    /// </summary>
    private async Task<ObjectResult> NotPersistedAsync(
        AuthorizationContext auth, string action, string targetId, UserAttributesNotPersistedException ex,
        CancellationToken ct)
    {
        _logger.LogError(ex, "Keycloak did not persist {Action} for user {UserId}", action, ForLog(targetId));
        await AuditAsync(auth, action, targetId, AdminAuditResult.Failure,
            new { error = ex.Message }, ct).ConfigureAwait(false);
        // `stored`: the role / permission attribute values Keycloak returned on the re-read.
        return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message, stored = ex.StoredAttributes });
    }

    /// <summary>
    /// Runs a permission write with the same failure handling as <see cref="UpdateAttributes"/>: 503 via the
    /// filter when unconfigured, 404 / 502 / 400 with a failure audit otherwise. A <c>null</c>
    /// <c>Failure</c> means the write landed and <c>Updated</c> holds the user; <c>Unverified</c> is set when
    /// Keycloak accepted the PUT but the verifying read failed (#532) — still a success for the caller, whose
    /// success audit carries the warning.
    /// </summary>
    private async Task<(ActionResult? Failure, EntraUser? Updated, UserAttributesWrittenUnverifiedException? Unverified)>
        TryWritePermissionAsync(
        AuthorizationContext auth, string action, string targetId, string permission,
        UpdateUserAttributesRequest request, CancellationToken ct)
    {
        try
        {
            return (null, await _userService.UpdateUserAttributesAsync(targetId, request, ct).ConfigureAwait(false), null);
        }
        catch (UserAttributesWrittenUnverifiedException ex)
        {
            LogUnverified(ex, action, targetId);
            return (null, ex.Written, ex);
        }
        catch (UserManagementUnavailableException)
        {
            // 503 via UserManagementUnavailableFilter, which also owns the audit (#293, #303).
            throw;
        }
        catch (UserNotFoundException)
        {
            await AuditAsync(auth, action, targetId, AdminAuditResult.Failure,
                new { permission, error = "not found" }, ct).ConfigureAwait(false);
            return (NotFound(), null, null);
        }
        catch (UserAttributesNotPersistedException ex)
        {
            return (await NotPersistedAsync(auth, action, targetId, ex, ct).ConfigureAwait(false), null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {Action} for user {UserId}", action, ForLog(targetId));
            await AuditAsync(auth, action, targetId, AdminAuditResult.Failure,
                new { permission, error = ex.Message }, ct).ConfigureAwait(false);
            return (BadRequest(new { error = ex.Message }), null, null);
        }
    }

    // A write Keycloak accepted is committed, so what follows it — the reverse-lookup mapping and the audit —
    // runs on CancellationToken.None in the unverified path: a cancelled request (one cause of the failed
    // verifying read) must not also drop the record that the write happened (#532).
    private void LogUnverified(UserAttributesWrittenUnverifiedException ex, string action, string targetId) =>
        _logger.LogWarning(ex,
            "Keycloak accepted {Action} for user {UserId} but it could not be verified; reporting it as written",
            action, ForLog(targetId));

    private static string UnverifiedWarning(UserAttributesWrittenUnverifiedException ex) =>
        $"written but unverified: {ex.InnerException?.Message ?? ex.Message}";

    private Task AuditAsync(
        AuthorizationContext auth, string action, string targetId,
        AdminAuditResult result, object? detail, CancellationToken ct)
    {
        var detailJson = detail is null ? null : JsonSerializer.Serialize(detail);
        var record = AdminAuditRecord.Create(
            AdminAuditSubjects.User, action, targetId, auth.UserId, actorName: null, result, detailJson);
        return _audit.RecordAsync(record, ct);
    }

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
    /// Strips CR/LF from a value before it goes into a log message.
    ///
    /// The user id arrives on the route, so a caller could put newlines in it and forge log lines
    /// that look like separate entries (CodeQL's log-injection rule flags exactly this). The audit
    /// record stores the same id as a field, where it is data rather than text and needs no
    /// escaping — this is only for the human-readable line.
    /// </summary>
    private static string ForLog(string value) =>
        value.Replace("\r", string.Empty).Replace("\n", string.Empty);

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
                "until the request is repeated", ForLog(targetId), action);

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
