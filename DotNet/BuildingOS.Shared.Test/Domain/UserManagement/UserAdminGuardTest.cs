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
}
