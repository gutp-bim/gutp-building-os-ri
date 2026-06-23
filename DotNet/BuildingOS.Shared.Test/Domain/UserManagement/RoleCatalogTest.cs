using BuildingOS.Shared.Domain.UserManagement;

namespace BuildingOS.Shared.Test.Domain.UserManagement;

public class RoleCatalogTest
{
    [Fact]
    public void Entries_ContainTheThreeFixedRoles()
    {
        var roles = RoleCatalog.Entries.Select(e => e.Role).ToList();
        Assert.Equal(new[] { "admin", "operator", "viewer" }, roles);
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
