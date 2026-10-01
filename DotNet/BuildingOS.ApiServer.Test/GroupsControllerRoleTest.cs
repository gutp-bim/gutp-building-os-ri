using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared.Domain.AdminAudit;
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
    private static GroupsController Build(
        string role, Mock<IGroupRepository>? repo = null, Mock<IAdminAuditRecorder>? audit = null, string userId = "svc")
    {
        repo ??= new Mock<IGroupRepository>();
        return new GroupsController(repo.Object, NullLogger<GroupsController>.Instance, (audit ?? new Mock<IAdminAuditRecorder>()).Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = userId, Role = role, Permissions = [] } },
                },
            },
        };
    }

    private static ResourceGroup G(string id, string? createdBy, params GroupResourceItem[] items) =>
        new() { Id = id, Name = id, CreatedBy = createdBy, ResourceItems = items.ToList() };

    private static Mock<IGroupRepository> Repo(params ResourceGroup[] groups)
    {
        var repo = new Mock<IGroupRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(groups);
        foreach (var g in groups)
        {
            repo.Setup(r => r.GetByIdAsync(g.Id, It.IsAny<CancellationToken>())).ReturnsAsync(g);
            repo.Setup(r => r.GetByIdWithItemsAsync(g.Id, It.IsAny<CancellationToken>())).ReturnsAsync(g);
        }
        repo.Setup(r => r.CreateAsync(It.IsAny<ResourceGroup>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceGroup g, CancellationToken _) => g);
        repo.Setup(r => r.AddResourceItemAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string gid, string t, string rid, CancellationToken _) => new GroupResourceItem { Id = "new", GroupId = gid, ResourceType = t, ResourceId = rid });
        return repo;
    }

    // ── Ownership (#506 review): a group-manager changes only the Groups it created ─────────────
    //
    // A Group's items are access grants for every user holding group:<id>:<actions>. Editing an
    // admin's Group (one users hold write on) would let a group-manager hand out control indirectly.

    [Fact]
    public async Task Create_RecordsTheCreator_AndShowsIt()
    {
        var repo = Repo();
        var result = await Build("group-manager", repo, userId: "svc-portal").Create(new GroupsController.CreateGroupRequest { Id = "t-a", Name = "A" }, default);

        repo.Verify(r => r.CreateAsync(It.Is<ResourceGroup>(g => g.CreatedBy == "svc-portal"), It.IsAny<CancellationToken>()));
        var body = Assert.IsType<GroupsController.GroupResponse>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal("svc-portal", body.CreatedBy);
    }

    /// <summary>
    /// A group-manager's Group ids live under a prefix derived from its subject. Users' grants match the
    /// plain id, so an id the caller could choose freely could take over grants that already name it
    /// (a deleted Group's leftovers, or ones provisioned ahead of the Group).
    /// </summary>
    [Fact]
    public async Task GroupManager_GroupIds_AreNamespacedByTheCaller()
    {
        var repo = Repo();
        var prefix = GroupsController.GroupIdPrefixFor("svc-portal");
        Assert.StartsWith("gm-", prefix);
        Assert.NotEqual(prefix, GroupsController.GroupIdPrefixFor("svc-other"));

        var created = await Build("group-manager", repo, userId: "svc-portal").Create(new GroupsController.CreateGroupRequest { Id = "tenant-a", Name = "A" }, default);
        var again = await Build("group-manager", repo, userId: "svc-portal").Create(new GroupsController.CreateGroupRequest { Id = prefix + "tenant-b", Name = "B" }, default);

        Assert.Equal(prefix + "tenant-a", Assert.IsType<GroupsController.GroupResponse>(Assert.IsType<CreatedAtActionResult>(created.Result).Value).Id);
        Assert.Equal(prefix + "tenant-b", Assert.IsType<GroupsController.GroupResponse>(Assert.IsType<CreatedAtActionResult>(again.Result).Value).Id);
        repo.Verify(r => r.CreateAsync(It.Is<ResourceGroup>(g => g.Id == "tenant-a"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Admin_GroupIds_AreTakenAsGiven()
    {
        var repo = Repo();
        var created = await Build("admin", repo).Create(new GroupsController.CreateGroupRequest { Id = "tenant-a", Name = "A" }, default);

        Assert.Equal("tenant-a", Assert.IsType<GroupsController.GroupResponse>(Assert.IsType<CreatedAtActionResult>(created.Result).Value).Id);
    }

    [Fact]
    public async Task GroupManager_SeesAndChangesOnlyItsOwnGroups()
    {
        var mine = G("t-a", "svc");
        var admins = G("ops", "admin-user");
        var legacy = G("old", null);   // created before ownership was recorded: admin-only
        var repo = Repo(mine, admins, legacy);
        var c = Build("group-manager", repo);

        var listed = Assert.IsAssignableFrom<IEnumerable<GroupsController.GroupResponse>>(
            Assert.IsType<OkObjectResult>((await c.GetAll(default)).Result).Value);
        Assert.Equal(["t-a"], listed.Select(g => g.Id));

        Assert.IsType<OkObjectResult>((await c.GetById("t-a", default)).Result);
        foreach (var other in new[] { "ops", "old" })
        {
            Assert.IsType<NotFoundResult>((await c.GetById(other, default)).Result);
            Assert.IsType<NotFoundResult>(await c.Update(other, new GroupsController.UpdateGroupRequest { Name = "x" }, default));
            Assert.IsType<NotFoundResult>(await c.Delete(other, default));
            Assert.IsType<NotFoundResult>((await c.AddResource(other, new GroupsController.AddResourceRequest { ResourceType = "building", ResourceId = "B1" }, default)).Result);
            Assert.IsType<NotFoundResult>((await c.AddResourcesBulk(other, new GroupsController.BulkAddResourceRequest { Items = [] }, default)).Result);
            Assert.IsType<NotFoundResult>(await c.RemoveResource(other, "i1", default));
        }
        repo.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.AddResourceItemAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Admin_StillManagesEveryGroup()
    {
        var repo = Repo(G("ops", "admin-user"), G("old", null), G("t-a", "svc"));
        var c = Build("admin", repo, userId: "another-admin");

        var listed = Assert.IsAssignableFrom<IEnumerable<GroupsController.GroupResponse>>(
            Assert.IsType<OkObjectResult>((await c.GetAll(default)).Result).Value);
        Assert.Equal(3, listed.Count());
        Assert.IsType<NoContentResult>(await c.Delete("old", default));
    }

    /// <summary>The item must belong to the group named in the route — for everyone, admins included.</summary>
    [Fact]
    public async Task RemoveResource_ItemOfAnotherGroup_IsNotFound()
    {
        var mine = G("t-a", "svc", new GroupResourceItem { Id = "i-mine", GroupId = "t-a", ResourceType = "space", ResourceId = "S1" });
        var theirs = G("t-b", "svc", new GroupResourceItem { Id = "i-theirs", GroupId = "t-b", ResourceType = "space", ResourceId = "S2" });
        var repo = Repo(mine, theirs);
        var c = Build("group-manager", repo);

        Assert.IsType<NotFoundResult>(await c.RemoveResource("t-a", "i-theirs", default));
        Assert.IsType<NoContentResult>(await c.RemoveResource("t-a", "i-mine", default));
        repo.Verify(r => r.RemoveResourceItemAsync("i-theirs", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Mutations_AreAudited()
    {
        var audit = new Mock<IAdminAuditRecorder>();
        var repo = Repo(G("t-a", "svc"));
        var c = Build("group-manager", repo, audit);

        await c.Create(new GroupsController.CreateGroupRequest { Id = "t-b", Name = "B" }, default);
        await c.AddResource("t-a", new GroupsController.AddResourceRequest { ResourceType = "space", ResourceId = "S1" }, default);
        await c.Delete("t-a", default);

        foreach (var action in new[] { "group-create", "group-add-resource", "group-delete" })
            audit.Verify(a => a.RecordAsync(
                It.Is<AdminAuditRecord>(r => r.SubjectType == "group" && r.Action == action && r.ActorSub == "svc"),
                It.IsAny<CancellationToken>()), Times.Once);
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
