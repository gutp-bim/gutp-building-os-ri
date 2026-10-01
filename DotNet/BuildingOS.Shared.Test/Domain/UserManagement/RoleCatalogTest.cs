using BuildingOS.Shared.Domain.UserManagement;

namespace BuildingOS.Shared.Test.Domain.UserManagement;

public class RoleCatalogTest
{
    [Fact]
    public void Entries_ContainTheFixedRoles()
    {
        var roles = RoleCatalog.Entries.Select(e => e.Role).ToList();
        Assert.Equal(new[] { "admin", "operator", "viewer" }, roles);
    }

    /// <summary>
    /// #506: group-manager is a client-credentials role, set on an application's service account in
    /// Keycloak — not a role the admin UI assigns to people, so it is not in the assignable catalog.
    /// </summary>
    [Fact]
    public void GroupManager_IsNotAssignableToUsers()
    {
        Assert.DoesNotContain(RoleCatalog.Entries, e => e.Role == "group-manager");
        Assert.False(RoleCatalog.IsAssignable("group-manager"));
        Assert.False(RoleCatalog.GrantsAdmin("group-manager"));
    }

    [Fact]
    public void OnlyAdminGrantsAdmin()
    {
        Assert.True(RoleCatalog.Entries.Single(e => e.Role == "admin").IsAdmin);
        Assert.False(RoleCatalog.Entries.Single(e => e.Role == "operator").IsAdmin);
        Assert.False(RoleCatalog.Entries.Single(e => e.Role == "viewer").IsAdmin);
    }

    [Fact]
    public void Workspaces_MirrorFrontendRoleMap()
    {
        // Must stay in sync with web-client/src/lib/auth/workspaces.ts ROLE_WORKSPACES.
        Assert.Equal(new[] { "operator", "admin", "platform" },
            RoleCatalog.Entries.Single(e => e.Role == "admin").Workspaces);
        Assert.Equal(new[] { "operator" },
            RoleCatalog.Entries.Single(e => e.Role == "operator").Workspaces);
        Assert.Equal(new[] { "operator" },
            RoleCatalog.Entries.Single(e => e.Role == "viewer").Workspaces);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("operator", true)]
    [InlineData("viewer", true)]
    [InlineData("group-manager", false)]
    [InlineData("Admin", false)]
    [InlineData("superuser", false)]
    [InlineData(null, false)]
    public void IsAssignable_AcceptsOnlyExactLowercaseRoles(string? role, bool expected)
    {
        Assert.Equal(expected, RoleCatalog.IsAssignable(role));
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("operator", false)]
    [InlineData("viewer", false)]
    [InlineData(null, false)]
    public void GrantsAdmin_OnlyForAdmin(string? role, bool expected)
    {
        Assert.Equal(expected, RoleCatalog.GrantsAdmin(role));
    }

    /// <summary>
    /// #506 review: when a user's groups carry different roles, authorization fails closed to the
    /// LEAST privileged one. "Any non-admin role" stopped meaning least privilege once group-manager
    /// (non-admin, but able to change Groups) existed. An unknown role grants nothing, so it is least.
    /// </summary>
    [Theory]
    [InlineData(new[] { "group-manager", "viewer" }, "viewer")]
    [InlineData(new[] { "admin", "group-manager" }, "group-manager")]
    [InlineData(new[] { "operator", "group-manager", "admin" }, "operator")]
    [InlineData(new[] { "group-manager", "superuser" }, "superuser")]
    [InlineData(new[] { "admin" }, "admin")]
    public void LeastPrivileged_PicksTheLowestRankedRole(string[] roles, string expected)
    {
        Assert.Equal(expected, RoleCatalog.LeastPrivileged(roles));
    }
}
