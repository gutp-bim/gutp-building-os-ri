using BuildingOS.Shared.Domain.Authorization;

namespace BuildingOS.Shared.Test.Domain.Authorization;

/// <summary>#506: what each role is allowed beyond its grants.</summary>
public class AuthorizationContextTest
{
    private static AuthorizationContext As(string role) => new() { UserId = "u", Role = role, Permissions = [] };

    [Theory]
    [InlineData("admin", true, true, true)]
    [InlineData("group-manager", false, true, true)]
    [InlineData("operator", false, false, false)]
    [InlineData("viewer", false, false, false)]
    [InlineData("Group-Manager", false, false, false)] // roles are exact, lowercase
    [InlineData("", false, false, false)]
    public void RoleCapabilities(string role, bool isAdmin, bool managesGroups, bool readsWholeTwinStructure)
    {
        var ctx = As(role);
        Assert.Equal(isAdmin, ctx.IsAdmin);
        Assert.Equal(managesGroups, ctx.CanManageGroups);
        Assert.Equal(readsWholeTwinStructure, ctx.ReadsWholeTwinStructure);
    }

    /// <summary>
    /// #506 review: a group-manager carries no permission strings, whatever the token says. Otherwise a
    /// human given the role who also holds group:X:write could add any building to X and control it.
    /// </summary>
    [Fact]
    public void GroupManager_HasNoPermissions_EvenIfTheTokenCarriesSome()
    {
        var ctx = new AuthorizationContext { UserId = "u", Role = "group-manager", Permissions = ["group:x:read,write"] };
        Assert.Empty(ctx.Permissions);

        var op = new AuthorizationContext { UserId = "u", Role = "operator", Permissions = ["group:x:read,write"] };
        Assert.Single(op.Permissions);
    }
}
