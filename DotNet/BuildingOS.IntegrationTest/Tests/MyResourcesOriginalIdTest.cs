using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Grouping.Entities;
using BuildingOs.ApiServer.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #504 B against real PostgreSQL: a Group member comes back as its business id, a direct grant the
/// admin UI recorded in the id-mapping table resolves through it, and a direct grant nobody recorded
/// is reported as unresolved rather than mixed in as a hash. The default response is unchanged.
/// </summary>
[Collection(Names.Postgres)]
public class MyResourcesOriginalIdTest(PostgresFixture postgres) : IntegrationTestBase
{
    [Fact]
    public async Task IdFormatOriginal_SeparatesBusinessIdsFromUnresolvedHashes()
    {
        await using var db = new RelationalDbContext(new DbContextOptionsBuilder<RelationalDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var groupId = $"tenant-{suffix}";
        var viaGroup = $"R501-{suffix}";
        var recorded = $"R502-{suffix}";
        var unrecorded = $"R503-{suffix}";
        var now = DateTime.UtcNow;
        db.ResourceGroups.Add(new ResourceGroup
        {
            Id = groupId, Name = "tenant", CreatedAt = now, UpdatedAt = now,
            ResourceItems = [new GroupResourceItem { Id = Guid.NewGuid().ToString("N"), ResourceType = "space", ResourceId = viaGroup, CreatedAt = now }],
        });
        await db.SaveChangesAsync();
        var mapping = new ResourceIdMappingRepository(db);
        await mapping.SaveMappingAsync("space", recorded);

        var authz = new DefaultAuthorizationService(
            new GroupMembershipResolver(new GroupRepository(db, NullLogger<GroupRepository>.Instance)),
            Mock.Of<IResourceHierarchyResolver>(),
            NullLogger<DefaultAuthorizationService>.Instance);
        var controller = new MyResourcesController(authz, mapping)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items =
                    {
                        ["AuthorizationContext"] = new AuthorizationContext
                        {
                            UserId = "u1", Role = "viewer",
                            Permissions =
                            [
                                PermissionHelper.BuildPermissionString("group", groupId, "read"),
                                PermissionHelper.BuildPermissionString("space", recorded, "read"),
                                PermissionHelper.BuildPermissionString("space", unrecorded, "read"),
                            ],
                        },
                    },
                },
            },
        };

        var original = Assert.IsType<MyResourcesResponse>(
            Assert.IsType<OkObjectResult>(await controller.GetMyResources(idFormat: "original")).Value);
        Assert.Equal(new[] { recorded, viaGroup }.OrderBy(x => x), original.Resources!["space"].OrderBy(x => x));
        Assert.Equal([PermissionHelper.HashResourceId(unrecorded)], original.Unresolved!["space"]);

        // Default: unchanged — whatever the mapping cannot resolve stays a hash, mixed in.
        var legacy = Assert.IsType<MyResourcesResponse>(
            Assert.IsType<OkObjectResult>(await controller.GetMyResources()).Value);
        Assert.Contains(recorded, legacy.Resources!["space"]);
        Assert.Contains(PermissionHelper.HashResourceId(viaGroup), legacy.Resources["space"]);
        Assert.Null(legacy.Unresolved);
    }
}
