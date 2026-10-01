using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Grouping.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// #506: the Groups API is open to <c>admin</c> and <c>group-manager</c> — the role an application's
/// service account gets so that keeping Groups in sync does not need full admin. Everyone else is 403.
/// </summary>
public class GroupsControllerRoleTest
{
    private static GroupsController Build(string role, Mock<IGroupRepository>? repo = null)
    {
        repo ??= new Mock<IGroupRepository>();
        return new GroupsController(repo.Object, NullLogger<GroupsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = "svc", Role = role, Permissions = [] } },
                },
            },
        };
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("group-manager")]
    public async Task ManagingRoles_CanListGroups(string role)
    {
        var repo = new Mock<IGroupRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ResourceGroup>());

        var result = await Build(role, repo).GetAll(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("viewer")]
    public async Task OtherRoles_AreForbiddenEverywhere(string role)
    {
        var c = Build(role);

        Assert.IsType<ForbidResult>((await c.GetAll(default)).Result);
        Assert.IsType<ForbidResult>((await c.GetById("g1", default)).Result);
        Assert.IsType<ForbidResult>((await c.Create(new GroupsController.CreateGroupRequest { Name = "x" }, default)).Result);
        Assert.IsType<ForbidResult>(await c.Update("g1", new GroupsController.UpdateGroupRequest { Name = "x" }, default));
        Assert.IsType<ForbidResult>(await c.Delete("g1", default));
        Assert.IsType<ForbidResult>(await c.RemoveResource("g1", "i1", default));
    }

    /// <summary>Every action, not only the list: a group-manager is never turned away by role.</summary>
    [Fact]
    public async Task GroupManager_IsNotForbiddenByAnyAction()
    {
        var repo = new Mock<IGroupRepository>();
        var c = Build("group-manager", repo);

        Assert.IsNotType<ForbidResult>((await c.GetById("g1", default)).Result);
        Assert.IsNotType<ForbidResult>(await c.Delete("g1", default));
        Assert.IsNotType<ForbidResult>(await c.RemoveResource("g1", "i1", default));
    }
}

/// <summary>#506: the unscoped structural lists (every floor / room / device / point) are open to a group-manager too.</summary>
public class StructuralListControllerRoleTest
{
    private static T WithRole<T>(T controller, string role) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = "svc", Role = role, Permissions = [] } },
            },
        };
        return controller;
    }

    [Theory]
    [InlineData("group-manager", false)]
    [InlineData("admin", false)]
    [InlineData("operator", true)]
    public async Task UnscopedLists(string role, bool forbidden)
    {
        var view = new Mock<BuildingOs.ApiServer.Authorization.IAuthorizedTwinView>();
        view.Setup(v => v.ListFloorsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        view.Setup(v => v.ListSpacesAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        view.Setup(v => v.ListDevicesAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        view.Setup(v => v.ListPointsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var floors = await WithRole(new FloorController(view.Object), role).List(null, default);

        Assert.Equal(forbidden, floors.Result is ForbidResult);
    }
}
