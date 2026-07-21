using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Grouping.Entities;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOs.ApiServer.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

[Collection(Names.Postgres)]
public class TelemetryBatchLatestAuthorizationTest(PostgresFixture postgres) : IntegrationTestBase
{
    [Fact]
    public async Task BatchLatest_ReturnsOnlyDirectAndGroupReadablePoints_WithRealPostgres()
    {
        var router = new Mock<ITelemetryQueryRouter>();
        router.Setup(candidate => candidate.QueryAsync(
                It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        await using var services = CreateServices(router.Object);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelationalDbContext>();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var directlyReadable = $"point-direct-{suffix}";
        var groupReadable = $"point-group-{suffix}";
        var denied = $"point-denied-{suffix}";
        var groupId = $"operators-{suffix}";
        var now = DateTime.UtcNow;
        db.ResourceGroups.Add(new ResourceGroup
        {
            Id = groupId,
            Name = "Integration test operators",
            CreatedAt = now,
            UpdatedAt = now,
            ResourceItems =
            [
                new GroupResourceItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ResourceType = "point",
                    ResourceId = groupReadable,
                    CreatedAt = now,
                },
            ],
        });
        await db.SaveChangesAsync();

        var controller = CreateController(
            scope.ServiceProvider,
            [
                PermissionHelper.BuildPermissionString("point", directlyReadable, "read"),
                PermissionHelper.BuildPermissionString("group", groupId, "read"),
            ]);

        var response = await controller.QueryBatchLatest(
            new BatchLatestRequest([directlyReadable, groupReadable, denied]),
            CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var samples = Assert.IsType<LatestSample[]>(result.Value);
        Assert.Equal([directlyReadable, groupReadable], samples.Select(sample => sample.PointId));
        router.Verify(candidate => candidate.QueryAsync(
            It.Is<TelemetryQueryRequest>(request => request.PointId == denied),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BatchLatest_Authorizes499GroupReadablePoints_WithOneRealDbContext()
    {
        var router = new Mock<ITelemetryQueryRouter>();
        router.Setup(candidate => candidate.QueryAsync(
                It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        await using var services = CreateServices(router.Object);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelationalDbContext>();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var groupId = $"large-operators-{suffix}";
        var pointIds = Enumerable.Range(0, 499)
            .Select(index => $"point-{index:D3}-{suffix}")
            .ToArray();
        var now = DateTime.UtcNow;
        db.ResourceGroups.Add(new ResourceGroup
        {
            Id = groupId,
            Name = "Large integration test operators",
            CreatedAt = now,
            UpdatedAt = now,
            ResourceItems = pointIds.Select(pointId => new GroupResourceItem
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceType = "point",
                ResourceId = pointId,
                CreatedAt = now,
            }).ToArray(),
        });
        await db.SaveChangesAsync();

        var controller = CreateController(
            scope.ServiceProvider,
            [PermissionHelper.BuildPermissionString("group", groupId, "read")]);

        var response = await controller.QueryBatchLatest(
            new BatchLatestRequest(pointIds),
            CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var samples = Assert.IsType<LatestSample[]>(result.Value);
        Assert.Equal(499, samples.Length);
        Assert.Equal(pointIds, samples.Select(sample => sample.PointId));
    }

    private ServiceProvider CreateServices(ITelemetryQueryRouter router)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RelationalDbContext>(options =>
            options.UseNpgsql(postgres.ConnectionString));
        services.AddScoped<IGroupRepository, GroupRepository>();
        services.AddScoped<IGroupMembershipResolver, GroupMembershipResolver>();
        services.AddScoped<IAuthorizationService, DefaultAuthorizationService>();
        var hierarchy = new Mock<IResourceHierarchyResolver>();
        hierarchy.Setup(resolver => resolver.GetAncestorsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(string ResourceType, string ResourceId)>());
        services.AddSingleton(hierarchy.Object);
        services.AddSingleton(Mock.Of<IDigitalTwinDatabase>());
        services.AddSingleton(Mock.Of<ITelemetryDatabase>());
        services.AddSingleton(router);
        services.AddScoped<TelemetryController>();
        return services.BuildServiceProvider();
    }

    private static TelemetryController CreateController(
        IServiceProvider services,
        IReadOnlyList<string> permissions)
    {
        var controller = services.GetRequiredService<TelemetryController>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Items =
                {
                    ["AuthorizationContext"] = new AuthorizationContext
                    {
                        UserId = "integration-operator",
                        Role = "operator",
                        Permissions = permissions,
                    },
                },
            },
        };
        return controller;
    }
}
