using BuildingOS.Shared.Domain.UserManagement;

namespace BuildingOS.Shared.Test.Domain.UserManagement;

public class RoleCatalogTest
{
    [Fact]
    public void Entries_ContainTheFixedRoles()
    {
        var roles = RoleCatalog.Entries.Select(e => e.Role).ToList();
        Assert.Equal(new[] { "admin", "operator", "viewer", "group-manager" }, roles);
    }

    /// <summary>
    /// #506: a role for an application's service account that keeps Groups in sync. It is not an
    /// admin and has no UI workspace (the web client has nothing for it to do).
    /// </summary>
    [Fact]
    public void GroupManager_IsNotAdmin_AndHasNoWorkspace()
    {
        var entry = RoleCatalog.Entries.Single(e => e.Role == "group-manager");
        Assert.False(entry.IsAdmin);
        Assert.Empty(entry.Workspaces);
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
        Assert.Empty(RoleCatalog.Entries.Single(e => e.Role == "group-manager").Workspaces);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("operator", true)]
    [InlineData("viewer", true)]
    [InlineData("group-manager", true)]
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
}
