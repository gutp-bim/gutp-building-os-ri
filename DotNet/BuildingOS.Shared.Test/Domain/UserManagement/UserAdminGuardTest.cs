using BuildingOS.Shared.Domain.UserManagement;

namespace BuildingOS.Shared.Test.Domain.UserManagement;

public class UserAdminGuardTest
{
    private static readonly IReadOnlyList<UserRoleState> TwoAdmins = new[]
    {
        new UserRoleState("admin-a", "admin", true),
        new UserRoleState("admin-b", "admin", true),
        new UserRoleState("op-1", "operator", true),
    };

    private static readonly IReadOnlyList<UserRoleState> OneAdmin = new[]
    {
        new UserRoleState("admin-a", "admin", true),
        new UserRoleState("op-1", "operator", true),
        new UserRoleState("viewer-1", "viewer", true),
    };

    // ── SetEnabled ───────────────────────────────────────────────────────────

    [Fact]
    public void SetEnabled_SelfDisable_IsBlocked()
    {
        var result = UserAdminGuard.CheckSetEnabled("admin-a", "admin-a", newEnabled: false, TwoAdmins);
        Assert.Equal(UserAdminGuardResult.SelfLockout, result);
    }

    [Fact]
    public void SetEnabled_DisablingLastAdmin_IsBlocked()
    {
        var result = UserAdminGuard.CheckSetEnabled("op-1", "admin-a", newEnabled: false, OneAdmin);
        Assert.Equal(UserAdminGuardResult.LastAdmin, result);
    }

    [Fact]
    public void SetEnabled_DisablingOneOfTwoAdmins_IsAllowed()
    {
        var result = UserAdminGuard.CheckSetEnabled("admin-b", "admin-a", newEnabled: false, TwoAdmins);
        Assert.Equal(UserAdminGuardResult.Allowed, result);
    }

    [Fact]
    public void SetEnabled_ReEnabling_IsAlwaysAllowed()
    {
        // Re-enabling self never locks out.
        var result = UserAdminGuard.CheckSetEnabled("admin-a", "admin-a", newEnabled: true, OneAdmin);
        Assert.Equal(UserAdminGuardResult.Allowed, result);
    }

    [Fact]
    public void SetEnabled_DisablingNonAdmin_IsAllowed()
    {
        var result = UserAdminGuard.CheckSetEnabled("admin-a", "op-1", newEnabled: false, OneAdmin);
        Assert.Equal(UserAdminGuardResult.Allowed, result);
    }

    [Fact]
    public void SetEnabled_AlreadyDisabledAdminNotCountedAsRemainingAdmin()
    {
        var users = new[]
        {
            new UserRoleState("admin-a", "admin", true),
            new UserRoleState("admin-b", "admin", false), // disabled → not an effective admin
        };
        var result = UserAdminGuard.CheckSetEnabled("admin-b", "admin-a", newEnabled: false, users);
        Assert.Equal(UserAdminGuardResult.LastAdmin, result);
    }

    // ── SetRole ──────────────────────────────────────────────────────────────

    [Fact]
    public void SetRole_SelfDemote_IsBlocked()
    {
        var result = UserAdminGuard.CheckSetRole("admin-a", "admin-a", "operator", TwoAdmins);
        Assert.Equal(UserAdminGuardResult.SelfLockout, result);
    }

    [Fact]
    public void SetRole_DemotingLastAdmin_IsBlocked()
    {
        var result = UserAdminGuard.CheckSetRole("op-1", "admin-a", "viewer", OneAdmin);
        Assert.Equal(UserAdminGuardResult.LastAdmin, result);
    }

    [Fact]
    public void SetRole_DemotingOneOfTwoAdmins_IsAllowed()
    {
        var result = UserAdminGuard.CheckSetRole("admin-b", "admin-a", "operator", TwoAdmins);
        Assert.Equal(UserAdminGuardResult.Allowed, result);
    }

    [Fact]
    public void SetRole_PromotingToAdmin_IsAllowed()
    {
        var result = UserAdminGuard.CheckSetRole("admin-a", "op-1", "admin", OneAdmin);
        Assert.Equal(UserAdminGuardResult.Allowed, result);
    }

    [Fact]
    public void SetRole_KeepingAdmin_IsAllowed()
    {
        // admin → admin is not a demotion.
        var result = UserAdminGuard.CheckSetRole("admin-a", "admin-a", "admin", OneAdmin);
        Assert.Equal(UserAdminGuardResult.Allowed, result);
    }

    [Fact]
    public void SetRole_NonAdminRoleChange_IsAllowed()
    {
        var result = UserAdminGuard.CheckSetRole("admin-a", "op-1", "viewer", OneAdmin);
        Assert.Equal(UserAdminGuardResult.Allowed, result);
    }

    // ── Group-derived admin (#519 follow-up) ─────────────────────────────────
    //
    // The non-aggregating building-os-role mapper falls back to a group's `role` when the user has
    // none of their own (realm.json: building-os-admins role=admin). Since #519 a role written through
    // /admin overrides that group value in the token, so the guard has to see the effective role.

    private static readonly IReadOnlyList<UserRoleState> GroupAdminOnly = new[]
    {
        new UserRoleState("group-admin", null, true, GroupRole: "admin"),
        new UserRoleState("op-1", "operator", true),
    };

    [Fact]
    public void SetRole_GroupDerivedAdmin_SelfDemote_IsBlocked()
    {
        var users = new[]
        {
            new UserRoleState("group-admin", null, true, GroupRole: "admin"),
            new UserRoleState("admin-b", "admin", true),
        };
        var result = UserAdminGuard.CheckSetRole("group-admin", "group-admin", "viewer", users);
        Assert.Equal(UserAdminGuardResult.SelfLockout, result);
    }

    [Fact]
    public void SetRole_DemotingTheOnlyGroupDerivedAdmin_IsBlocked()
    {
        var result = UserAdminGuard.CheckSetRole("op-1", "group-admin", "operator", GroupAdminOnly);
        Assert.Equal(UserAdminGuardResult.LastAdmin, result);
    }

    [Fact]
    public void SetRole_GroupDerivedAdminCountsAsARemainingAdmin()
    {
        var users = new[]
        {
            new UserRoleState("admin-a", "admin", true),
            new UserRoleState("group-admin", null, true, GroupRole: "admin"),
        };
        var result = UserAdminGuard.CheckSetRole("group-admin", "admin-a", "viewer", users);
        Assert.Equal(UserAdminGuardResult.Allowed, result);
    }

    [Fact]
    public void SetEnabled_DisablingTheOnlyGroupDerivedAdmin_IsBlocked()
    {
        var result = UserAdminGuard.CheckSetEnabled("op-1", "group-admin", newEnabled: false, GroupAdminOnly);
        Assert.Equal(UserAdminGuardResult.LastAdmin, result);
    }

    [Fact]
    public void SetRole_OwnRoleOverridesTheGroupRole()
    {
        // The user's own attribute wins over the group in the token, so this user is not an admin.
        var users = new[]
        {
            new UserRoleState("admin-a", "admin", true),
            new UserRoleState("op-in-admin-group", "operator", true, GroupRole: "admin"),
        };
        var result = UserAdminGuard.CheckSetRole("op-in-admin-group", "admin-a", "viewer", users);
        Assert.Equal(UserAdminGuardResult.LastAdmin, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void SetRole_ClearingOwnRole_FallsBackToTheGroupRole(string newRole)
    {
        // Clearing an own "admin" while a group also grants admin keeps the user an admin.
        var users = new[] { new UserRoleState("admin-a", "admin", true, GroupRole: "admin") };
        var result = UserAdminGuard.CheckSetRole("admin-a", "admin-a", newRole, users);
        Assert.Equal(UserAdminGuardResult.Allowed, result);
    }

    [Fact]
    public void SetRole_ClearingOwnAdminWithoutAGroupAdmin_IsADemotion()
    {
        var result = UserAdminGuard.CheckSetRole("admin-a", "admin-a", "", OneAdmin);
        Assert.Equal(UserAdminGuardResult.SelfLockout, result);
    }
}
