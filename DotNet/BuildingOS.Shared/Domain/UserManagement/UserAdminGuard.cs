namespace BuildingOS.Shared.Domain.UserManagement;

/// <summary>Snapshot of one user's role/enabled state, used by <see cref="UserAdminGuard"/>.</summary>
/// <param name="Id">Keycloak user id.</param>
/// <param name="Role">
/// The user's own <c>role</c> attribute (<see cref="KeycloakUserAttributes.ReadMappedRole"/>): its first
/// value exactly as stored. Any non-empty value — even whitespace — reaches the token and shadows the
/// group role.
/// </param>
/// <param name="Enabled">Whether the account can authenticate.</param>
/// <param name="GroupRole">
/// The role the user inherits from a Keycloak group's (or a parent group's) <c>role</c> attribute, if
/// any. The realm's <c>building-os-role</c> mapper is non-aggregating: the user's own attribute wins, and
/// only when it is absent does ONE group's value reach the <c>building_os_role</c> claim — Keycloak does
/// not define which. So this is conservative: <c>admin</c> if any group grants admin.
/// </param>
/// <param name="GroupRoleAmbiguous">
/// The user's role-carrying groups disagree, so which one reaches the token is undefined.
/// </param>
/// <param name="LegacyRole">
/// The pre-#519 <c>buildingos_role</c>. Reaches authorization only via the Admin-API fallback, i.e. with
/// neither an own nor a group role.
/// </param>
/// <param name="GroupRoleUnknown">
/// The user's groups could not be read (#532), so <see cref="GroupRole"/> is <c>null</c> because it is
/// unknown, not because there is none. Matters only without an own role (which hides the group role) or
/// when the own role is being cleared; the guard then fails safe (<see cref="MayBeAdmin"/>).
/// </param>
public sealed record UserRoleState(
    string Id,
    string? Role,
    bool Enabled,
    string? GroupRole = null,
    bool GroupRoleAmbiguous = false,
    string? LegacyRole = null,
    bool GroupRoleUnknown = false)
{
    /// <summary>
    /// The role that reaches authorization — own, else group (conservative), else legacy — compared
    /// exactly as <c>AuthorizationContext.IsAdmin</c> does.
    /// </summary>
    public string? EffectiveRole => KeycloakUserAttributes.ResolveRole(Role, GroupRole, LegacyRole);

    /// <summary>
    /// Whether this user is an admin whichever of their groups Keycloak picks: an own <c>admin</c>, a group
    /// <c>admin</c> every role-carrying group agrees on, or — with neither — a legacy <c>admin</c>.
    /// </summary>
    public bool IsUnambiguouslyAdmin =>
        RoleCatalog.GrantsAdmin(EffectiveRole)
        && (!string.IsNullOrEmpty(Role)
            || (!GroupRoleUnknown && (string.IsNullOrEmpty(GroupRole) || !GroupRoleAmbiguous)));

    /// <summary>
    /// Whether this user could be an admin: <see cref="EffectiveRole"/> grants admin, or — without an own
    /// role — the groups that decide it could not be read (#532). The conservative view of a guard target.
    /// </summary>
    public bool MayBeAdmin =>
        RoleCatalog.GrantsAdmin(EffectiveRole) || (string.IsNullOrEmpty(Role) && GroupRoleUnknown);
}

/// <summary>Outcome of a lockout-prevention check.</summary>
public enum UserAdminGuardResult
{
    /// <summary>The operation is safe to apply.</summary>
    Allowed,

    /// <summary>The actor is acting on their own account in a way that would lock themselves out.</summary>
    SelfLockout,

    /// <summary>The operation would remove the last remaining enabled admin.</summary>
    LastAdmin,
}

/// <summary>
/// Pure guards that prevent an admin from accidentally locking everyone (or themselves) out of the
/// admin surface by disabling / demoting the last enabled admin. No I/O — the caller supplies the
/// current user snapshot.
/// <para>
/// The checks are staged so the caller resolves only what is needed: <see cref="SetEnabledNeedsGuard"/> /
/// <see cref="SetRoleNeedsGuard"/> need nothing, <see cref="PreCheckSetEnabled"/> /
/// <see cref="PreCheckSetRole"/> need the target alone, and only when they return <c>null</c> does
/// <see cref="CheckLastAdmin"/> need every user.
/// </para>
/// <para>
/// The target is judged conservatively (an admin if any group grants admin, so they cannot lock
/// themselves out); the other users count as remaining admins only when unambiguously admin
/// (<see cref="UserRoleState.IsUnambiguouslyAdmin"/>).
/// </para>
/// <para>
/// This is a best-effort UX safety net, not an authorization boundary: it operates on the snapshot
/// the caller passes (today <c>IUserManagementService.GetUserRoleStatesAsync</c>, which is capped at 100 users and
/// is not read under a transaction). A genuine "always keep one admin" invariant would require a
/// transactional/paginated check; access can always be restored directly in Keycloak.
/// </para>
/// </summary>
public static class UserAdminGuard
{
    /// <summary>Only disabling can lock anyone out; re-enabling needs no check.</summary>
    public static bool SetEnabledNeedsGuard(bool newEnabled) => !newEnabled;

    /// <summary>
    /// A role write can lock out only when it sets a role that is not admin (or clears it): <c>null</c>
    /// leaves the role alone and an own <c>admin</c> is an admin whatever the groups say.
    /// </summary>
    public static bool SetRoleNeedsGuard(string? newRole) => newRole != null && !RoleCatalog.GrantsAdmin(newRole);

    /// <summary>
    /// Clearing the role makes the token fall back to the group role, so the target's
    /// <see cref="UserRoleState.GroupRole"/> must be resolved even though their own role hides it today
    /// (the service skips group lookups for users with an own role).
    /// </summary>
    public static bool SetRoleNeedsTargetGroupRole(string? newRole) => newRole != null && string.IsNullOrWhiteSpace(newRole);

    /// <summary>
    /// What can be decided about disabling <paramref name="target"/> from the target alone; <c>null</c>
    /// means the last-admin count (<see cref="CheckLastAdmin"/>) is needed.
    /// </summary>
    public static UserAdminGuardResult? PreCheckSetEnabled(string actorSub, UserRoleState target, bool newEnabled)
    {
        if (!SetEnabledNeedsGuard(newEnabled)) return UserAdminGuardResult.Allowed;
        if (string.Equals(actorSub, target.Id, StringComparison.Ordinal)) return UserAdminGuardResult.SelfLockout;
        // Disabling a non-admin cannot remove the last admin; an unreadable group role may be admin (#532).
        return target.MayBeAdmin ? null : UserAdminGuardResult.Allowed;
    }

    /// <summary>
    /// What can be decided about setting <paramref name="target"/>'s role to <paramref name="newRole"/>
    /// from the target alone; <c>null</c> means the last-admin count is needed.
    /// </summary>
    public static UserAdminGuardResult? PreCheckSetRole(string actorSub, UserRoleState target, string? newRole)
    {
        if (!SetRoleNeedsGuard(newRole)) return UserAdminGuardResult.Allowed;

        // Before: conservative (an admin from any group is an admin). After: an explicit role write
        // replaces the own role and removes the legacy one, so a cleared own role falls back to the
        // groups — which keeps the user an admin only if every role-carrying group agrees on admin.
        // An unreadable group role (#532) counts as admin before and as not admin after: fail safe.
        var wasAdmin = target.MayBeAdmin;
        var willBeAdmin = string.IsNullOrWhiteSpace(newRole)
            ? !target.GroupRoleAmbiguous && !target.GroupRoleUnknown && RoleCatalog.GrantsAdmin(target.GroupRole)
            : RoleCatalog.GrantsAdmin(newRole);

        // Only a demotion away from admin can cause lockout.
        if (!wasAdmin || willBeAdmin) return UserAdminGuardResult.Allowed;
        if (string.Equals(actorSub, target.Id, StringComparison.Ordinal)) return UserAdminGuardResult.SelfLockout;
        return null;
    }

    /// <summary>
    /// <see cref="UserAdminGuardResult.LastAdmin"/> when, once <paramref name="targetId"/> stops being an
    /// admin, no other enabled admin remains. Others count only when unambiguously admin — except the
    /// acting admin (<paramref name="actorSub"/>, when not the target): the caller has already checked their
    /// token is admin, which proves which of their groups' values Keycloak emits, so they count whenever
    /// the snapshot still makes them an enabled admin at all (a stale token for an account since demoted or
    /// disabled does not). That includes an actor whose own groups could not be read (#532,
    /// <see cref="UserRoleState.MayBeAdmin"/>): the token proves the admin role those groups carry.
    /// </summary>
    public static UserAdminGuardResult CheckLastAdmin(
        string targetId, IReadOnlyList<UserRoleState> allUsers, string? actorSub = null) =>
        allUsers.Any(u =>
            u.Enabled
            && !string.Equals(u.Id, targetId, StringComparison.Ordinal)
            && (u.IsUnambiguouslyAdmin
                || (string.Equals(u.Id, actorSub, StringComparison.Ordinal) && u.MayBeAdmin)))
            ? UserAdminGuardResult.Allowed
            : UserAdminGuardResult.LastAdmin;

    /// <summary>Check enabling/disabling <paramref name="targetId"/> against the full snapshot.</summary>
    public static UserAdminGuardResult CheckSetEnabled(
        string actorSub,
        string targetId,
        bool newEnabled,
        IReadOnlyList<UserRoleState> allUsers) =>
        PreCheckSetEnabled(actorSub, FindTarget(targetId, allUsers), newEnabled)
        ?? CheckLastAdmin(targetId, allUsers, actorSub);

    /// <summary>Check changing <paramref name="targetId"/>'s role to <paramref name="newRole"/> against the full snapshot.</summary>
    public static UserAdminGuardResult CheckSetRole(
        string actorSub,
        string targetId,
        string? newRole,
        IReadOnlyList<UserRoleState> allUsers) =>
        PreCheckSetRole(actorSub, FindTarget(targetId, allUsers), newRole)
        ?? CheckLastAdmin(targetId, allUsers, actorSub);

    private static UserRoleState FindTarget(string targetId, IReadOnlyList<UserRoleState> allUsers) =>
        allUsers.FirstOrDefault(u => string.Equals(u.Id, targetId, StringComparison.Ordinal))
        ?? new UserRoleState(targetId, null, true);
}
