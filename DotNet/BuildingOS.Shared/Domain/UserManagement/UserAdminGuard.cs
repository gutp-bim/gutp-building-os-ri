namespace BuildingOS.Shared.Domain.UserManagement;

/// <summary>Snapshot of one user's role/enabled state, used by <see cref="UserAdminGuard"/>.</summary>
public sealed record UserRoleState(string Id, string? Role, bool Enabled);

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
/// This is a best-effort UX safety net, not an authorization boundary: it operates on the snapshot
/// the caller passes (today the controller's <c>GetUsersAsync</c>, which is capped at 100 users and
/// is not read under a transaction). A genuine "always keep one admin" invariant would require a
/// transactional/paginated check; access can always be restored directly in Keycloak.
/// </para>
/// </summary>
public static class UserAdminGuard
{
    /// <summary>Check enabling/disabling <paramref name="targetId"/>.</summary>
    public static UserAdminGuardResult CheckSetEnabled(
        string actorSub,
        string targetId,
        bool newEnabled,
        IReadOnlyList<UserRoleState> allUsers)
    {
        if (newEnabled)
        {
            // Re-enabling never causes lockout.
            return UserAdminGuardResult.Allowed;
        }

        if (string.Equals(actorSub, targetId, StringComparison.Ordinal))
        {
            return UserAdminGuardResult.SelfLockout;
        }

        return WouldRemoveLastAdmin(targetId, allUsers, targetStaysAdmin: false)
            ? UserAdminGuardResult.LastAdmin
            : UserAdminGuardResult.Allowed;
    }

    /// <summary>Check changing <paramref name="targetId"/>'s role to <paramref name="newRole"/>.</summary>
    public static UserAdminGuardResult CheckSetRole(
        string actorSub,
        string targetId,
        string? newRole,
        IReadOnlyList<UserRoleState> allUsers)
    {
        var target = allUsers.FirstOrDefault(u => string.Equals(u.Id, targetId, StringComparison.Ordinal));
        var wasAdmin = RoleCatalog.GrantsAdmin(target?.Role);
        var willBeAdmin = RoleCatalog.GrantsAdmin(newRole);

        // Only a demotion away from admin can cause lockout.
        if (!wasAdmin || willBeAdmin)
        {
            return UserAdminGuardResult.Allowed;
        }

        if (string.Equals(actorSub, targetId, StringComparison.Ordinal))
        {
            return UserAdminGuardResult.SelfLockout;
        }

        return WouldRemoveLastAdmin(targetId, allUsers, targetStaysAdmin: false)
            ? UserAdminGuardResult.LastAdmin
            : UserAdminGuardResult.Allowed;
    }

    /// <summary>
    /// True if, after the target stops being an effective admin, no other enabled admin remains.
    /// An "effective admin" is enabled AND has the admin role.
    /// </summary>
    private static bool WouldRemoveLastAdmin(
        string targetId,
        IReadOnlyList<UserRoleState> allUsers,
        bool targetStaysAdmin)
    {
        var remainingAdmins = allUsers.Count(u =>
            u.Enabled
            && RoleCatalog.GrantsAdmin(u.Role)
            && !string.Equals(u.Id, targetId, StringComparison.Ordinal));

        if (targetStaysAdmin)
        {
            remainingAdmins++;
        }

        return remainingAdmins == 0;
    }
}
