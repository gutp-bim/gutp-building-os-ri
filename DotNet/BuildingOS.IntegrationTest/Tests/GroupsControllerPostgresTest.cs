using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Infrastructure.AdminAudit;
using BuildingOs.ApiServer.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #506 against real PostgreSQL, with the Group repository and the admin audit sharing one
/// DbContext as they do per request: a failed item insert must not poison what is saved after it.
/// </summary>
[Collection(Names.Postgres)]
public class GroupsControllerPostgresTest(PostgresFixture postgres) : IntegrationTestBase
{
    private async Task<(GroupsController Controller, RelationalDbContext Db)> BuildAsync(string role, string sub)
    {
        var db = new RelationalDbContext(new DbContextOptionsBuilder<RelationalDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        await db.Database.MigrateAsync();
        var controller = new GroupsController(
            new GroupRepository(db, NullLogger<GroupRepository>.Instance),
            NullLogger<GroupsController>.Instance,
            new EfAdminAuditRecorder(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = sub, Role = role, Permissions = [] } },
                },
            },
        };
        return (controller, db);
    }

    [Fact]
    public async Task BulkAdd_WithADuplicate_AddsTheRest_ReportsTheDuplicate_AndIsAudited()
    {
        var sub = "svc-" + Guid.NewGuid().ToString("N");
        var (c, db) = await BuildAsync("group-manager", sub);
        await using var _ = db;
        var created = (GroupsController.GroupResponse)((CreatedAtActionResult)(await c.Create(
            new GroupsController.CreateGroupRequest { Id = "tenant", Name = "Tenant" }, default)).Result!).Value!;
        await c.AddResource(created.Id, new GroupsController.AddResourceRequest { ResourceType = "space", ResourceId = "S1" }, default);

        var result = await c.AddResourcesBulk(created.Id, new GroupsController.BulkAddResourceRequest
        {
            Items =
            [
                new() { ResourceType = "space", ResourceId = "S1" },   // already in the Group
                new() { ResourceType = "space", ResourceId = "S2" },
                new() { ResourceType = "space", ResourceId = "S3" },
            ],
        }, default);

        var body = Assert.IsType<GroupsController.BulkAddResourceResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["S2", "S3"], body.Added.Select(a => a.ResourceId).Order());
        Assert.Equal(["space:S1"], body.Failed);
        Assert.True(await db.AdminAudits.AnyAsync(a => a.ActorSub == sub && a.Action == "group-add-resources-bulk"));
    }

    [Fact]
    public async Task GroupManager_ListsOnlyItsOwnGroups()
    {
        var sub = "svc-" + Guid.NewGuid().ToString("N");
        var (mine, db) = await BuildAsync("group-manager", sub);
        await using var _ = db;
        await mine.Create(new GroupsController.CreateGroupRequest { Id = "a", Name = "A" }, default);
        var (admin, adminDb) = await BuildAsync("admin", "admin-" + Guid.NewGuid().ToString("N"));
        await using var __ = adminDb;
        await admin.Create(new GroupsController.CreateGroupRequest { Id = "admin-" + Guid.NewGuid().ToString("N"), Name = "Admin" }, default);

        var listed = (IEnumerable<GroupsController.GroupResponse>)((OkObjectResult)(await mine.GetAll(default)).Result!).Value!;

        Assert.All(listed, g => Assert.Equal(sub, g.CreatedBy));
        Assert.Single(listed);
    }
}
