using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Grouping.Entities;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.Authorization;
using BuildingOs.ApiServer.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #504 end to end, against a real twin (OxiGraph) and real Group store (PostgreSQL): a user whose
/// Group holds <c>space:R501</c> — a business id, as Group items are stored — can walk the hierarchy
/// under R501 through the dtId-addressed reads, sees nothing under the neighbouring room, and the
/// telemetry check (point by business id, ancestors via sbco:id) agrees with the tree.
/// </summary>
[Collection(Names.Postgres)]
public class HierarchyBusinessIdAuthorizationTest(PostgresFixture postgres, OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    private const string Ns = "https://www.sbco.or.jp/ont/resource/it504-";

    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Iri(string id) => Ns + id;

    private Task SeedTwinAsync() => oxiGraph.Client.UpdateAsync($$"""
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        INSERT DATA {
          <{{Iri("B1")}}> a sbco:Building ; sbco:id "B1" ; sbco:name "Tower" ; sbco:hasPart <{{Iri("F5")}}> .
          <{{Iri("F5")}}> a sbco:Level ; sbco:id "F5" ; sbco:name "5F" ;
              sbco:hasPart <{{Iri("R501")}}> , <{{Iri("R502")}}> .
          <{{Iri("R501")}}> a sbco:Room ; sbco:id "R501" ; sbco:name "501" .
          <{{Iri("R502")}}> a sbco:Room ; sbco:id "R502" ; sbco:name "502" .
          <{{Iri("AHU-501")}}> a sbco:EquipmentExt ; sbco:id "AHU-501" ; sbco:name "AHU 501" ;
              sbco:locatedIn <{{Iri("R501")}}> ; sbco:hasPoint <{{Iri("PT-501")}}> .
          <{{Iri("AHU-502")}}> a sbco:EquipmentExt ; sbco:id "AHU-502" ; sbco:name "AHU 502" ;
              sbco:locatedIn <{{Iri("R502")}}> ; sbco:hasPoint <{{Iri("PT-502")}}> .
          <{{Iri("PT-501")}}> a sbco:PointExt ; sbco:id "P-501-KWH" ; sbco:name "kWh 501" .
          <{{Iri("PT-502")}}> a sbco:PointExt ; sbco:id "P-502-KWH" ; sbco:name "kWh 502" .
        }
        """);

    [Fact]
    public async Task GroupGrantOnASpaceBusinessId_WalksTheTreeUnderIt_AndOnlyThere()
    {
        await SeedTwinAsync();
        await using var db = new RelationalDbContext(new DbContextOptionsBuilder<RelationalDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        await db.Database.MigrateAsync();

        var groupId = $"tenant-a-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        db.ResourceGroups.Add(new ResourceGroup
        {
            Id = groupId, Name = "Tenant A", CreatedAt = now, UpdatedAt = now,
            ResourceItems =
            [
                new GroupResourceItem { Id = Guid.NewGuid().ToString("N"), ResourceType = "space", ResourceId = "R501", CreatedAt = now },
            ],
        });
        await db.SaveChangesAsync();

        var (authz, view) = Stack(db);
        var user = new AuthorizationContext
        {
            UserId = "u-office-a", Role = "viewer",
            Permissions = [PermissionHelper.BuildPermissionString("group", groupId, "read")],
        };

        // The room itself and what is under it.
        Assert.IsType<TwinGetResult<Space>.Ok>(await view.GetSpaceAsync(user, Iri("R501"), default));
        var devices = await view.ListDevicesAsync(user, Iri("R501"), default);
        Assert.Equal(["AHU-501"], devices.Select(d => d.Id));
        var points = await view.ListPointsAsync(user, Iri("AHU-501"), default);
        Assert.Equal(["P-501-KWH"], points.Select(p => p.Id));

        // Not the neighbouring room, and not the floor or building above.
        Assert.Empty(await view.ListDevicesAsync(user, Iri("R502"), default));
        Assert.IsType<TwinGetResult<Space>.Forbidden>(await view.GetSpaceAsync(user, Iri("R502"), default));
        Assert.IsType<TwinGetResult<Floor>.Forbidden>(await view.GetFloorAsync(user, Iri("F5"), default));
        Assert.Empty(await view.ListBuildingsAsync(user, default));

        // The telemetry path answers the same question the same way.
        Assert.True(await authz.CanAccessAsync(user, "point", "P-501-KWH", "read"));
        Assert.False(await authz.CanAccessAsync(user, "point", "P-502-KWH", "read"));
    }

    [Fact]
    public async Task BuildingGrantOnTheBusinessId_WalksEveryLevelBelow_ThroughTheRealAncestorChain()
    {
        await SeedTwinAsync();
        await using var db = await NewDbAsync();
        var (_, view) = Stack(db);
        var user = new AuthorizationContext
        {
            UserId = "u-bm-b1", Role = "viewer",
            Permissions = [PermissionHelper.BuildPermissionString("building", "B1", "read")],
        };

        Assert.Equal(["B1"], (await view.ListBuildingsAsync(user, default)).Select(b => b.Id));
        Assert.Equal(["F5"], (await view.ListFloorsAsync(user, Iri("B1"), default)).Select(f => f.Id));
        Assert.Equal(2, (await view.ListSpacesAsync(user, Iri("F5"), default)).Length);
        Assert.Equal(["AHU-502"], (await view.ListDevicesAsync(user, Iri("R502"), default)).Select(d => d.Id));
        Assert.Equal(["P-502-KWH"], (await view.ListPointsAsync(user, Iri("AHU-502"), default)).Select(p => p.Id));
        Assert.IsType<TwinGetResult<Space>.Ok>(await view.GetSpaceAsync(user, Iri("R502"), default));
        Assert.Equal(2, (await view.ListPointDetailsAsync(user, Iri("B1"), default)).Length);
    }

    [Fact]
    public async Task FloorGrantOnTheBusinessId_ReachesItsRooms_NotTheBuilding()
    {
        await SeedTwinAsync();
        await using var db = await NewDbAsync();
        var (_, view) = Stack(db);
        var user = new AuthorizationContext
        {
            UserId = "u-floor", Role = "viewer",
            Permissions = [PermissionHelper.BuildPermissionString("floor", "F5", "read")],
        };

        Assert.Equal(2, (await view.ListSpacesAsync(user, Iri("F5"), default)).Length);
        Assert.Equal(["AHU-501"], (await view.ListDevicesAsync(user, Iri("R501"), default)).Select(d => d.Id));
        Assert.IsType<TwinGetResult<Building>.Forbidden>(await view.GetBuildingAsync(user, Iri("B1"), default));
        Assert.Empty(await view.ListBuildingsAsync(user, default));
    }

    // ── #548: the granted node's ancestors become navigable ───────────────────────────────────

    /// <summary>
    /// The same Group grant on <c>space:R501</c>, now with the navigable-ancestor resolver wired as the
    /// API server wires it: the user walks building → floor → room from the top of the tree, sees only
    /// R501 on 5F (not R502), and still cannot read the building or floor themselves.
    /// </summary>
    [Fact]
    public async Task GroupGrantOnASpace_MakesItsBuildingAndFloorNavigable_AndNothingElse()
    {
        await SeedTwinAsync();
        await using var db = await NewDbAsync();
        var groupId = $"tenant-a-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        db.ResourceGroups.Add(new ResourceGroup
        {
            Id = groupId, Name = "Tenant A", CreatedAt = now, UpdatedAt = now,
            ResourceItems =
            [
                new GroupResourceItem { Id = Guid.NewGuid().ToString("N"), ResourceType = "space", ResourceId = "R501", CreatedAt = now },
            ],
        });
        await db.SaveChangesAsync();
        var (_, view) = Stack(db, navigable: true);
        var user = new AuthorizationContext
        {
            UserId = "u-office-a", Role = "viewer",
            Permissions = [PermissionHelper.BuildPermissionString("group", groupId, "read")],
        };

        Assert.Equal(["B1"], (await view.ListBuildingsAsync(user, default)).Select(b => b.Id));
        Assert.Equal(["F5"], (await view.ListFloorsAsync(user, Iri("B1"), default)).Select(f => f.Id));
        Assert.Equal(["R501"], (await view.ListSpacesAsync(user, Iri("F5"), default)).Select(x => x.Id));
        Assert.Equal(["AHU-501"], (await view.ListDevicesAsync(user, Iri("R501"), default)).Select(d => d.Id));

        Assert.IsType<TwinGetResult<Building>.Forbidden>(await view.GetBuildingAsync(user, Iri("B1"), default));
        Assert.IsType<TwinGetResult<Floor>.Forbidden>(await view.GetFloorAsync(user, Iri("F5"), default));
        Assert.Empty(await view.ListDevicesAsync(user, Iri("R502"), default));
    }

    /// <summary>
    /// A direct floor grant stores only a hash; the admin UI records its business id in the id-mapping
    /// table, which is what places it in the twin. The building above becomes navigable.
    /// </summary>
    [Fact]
    public async Task DirectFloorGrant_ResolvedThroughTheMappingTable_MakesItsBuildingNavigable()
    {
        await SeedTwinAsync();
        await using var db = await NewDbAsync();
        await new ResourceIdMappingRepository(db).SaveMappingAsync("floor", "F5", "5F");
        var (_, view) = Stack(db, navigable: true);
        var user = new AuthorizationContext
        {
            UserId = "u-floor", Role = "viewer",
            Permissions = [PermissionHelper.BuildPermissionString("floor", "F5", "read")],
        };

        Assert.Equal(["B1"], (await view.ListBuildingsAsync(user, default)).Select(b => b.Id));
        Assert.Equal(["F5"], (await view.ListFloorsAsync(user, Iri("B1"), default)).Select(f => f.Id));
        Assert.Equal(2, (await view.ListSpacesAsync(user, Iri("F5"), default)).Length);
    }

    /// <summary>A point grant makes its device, room, floor and building navigable — not the sibling room.</summary>
    [Fact]
    public async Task PointGrant_MakesItsWholeChainNavigable()
    {
        await SeedTwinAsync();
        await using var db = await NewDbAsync();
        await new ResourceIdMappingRepository(db).SaveMappingAsync("point", "P-502-KWH");
        var (_, view) = Stack(db, navigable: true);
        var user = new AuthorizationContext
        {
            UserId = "u-point", Role = "viewer",
            Permissions = [PermissionHelper.BuildPermissionString("point", "P-502-KWH", "read")],
        };

        Assert.Equal(["B1"], (await view.ListBuildingsAsync(user, default)).Select(b => b.Id));
        Assert.Equal(["R502"], (await view.ListSpacesAsync(user, Iri("F5"), default)).Select(x => x.Id));
        Assert.Equal(["AHU-502"], (await view.ListDevicesAsync(user, Iri("R502"), default)).Select(d => d.Id));
        Assert.Equal(["P-502-KWH"], (await view.ListPointsAsync(user, Iri("AHU-502"), default)).Select(p => p.Id));
    }

    private async Task<RelationalDbContext> NewDbAsync()
    {
        var db = new RelationalDbContext(new DbContextOptionsBuilder<RelationalDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    /// <summary>The resolver's own scope, holding the same real services the API server registers.</summary>
    private static IServiceScopeFactory NavigationScope(
        DefaultAuthorizationService authz, RelationalDbContext db, OxiGraphHierarchyResolver hierarchy)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthorizationService>(authz);
        services.AddSingleton<IResourceIdMappingRepository>(new ResourceIdMappingRepository(db));
        services.AddSingleton<IResourceHierarchyResolver>(hierarchy);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private (DefaultAuthorizationService Authz, AuthorizedTwinView View) Stack(
        RelationalDbContext db, bool navigable = false)
    {
        var hierarchy = new OxiGraphHierarchyResolver(oxiGraph.Client);
        var authz = new DefaultAuthorizationService(
            new GroupMembershipResolver(new GroupRepository(db, NullLogger<GroupRepository>.Instance)),
            hierarchy,
            NullLogger<DefaultAuthorizationService>.Instance);
        var view = new AuthorizedTwinView(
            new OxiGraphDigitalTwinDatabase(oxiGraph.Client, new MemoryCache(new MemoryCacheOptions())), authz,
            navigable: navigable
                ? new NavigableAncestorResolver(NavigationScope(authz, db, hierarchy),
                    new MemoryCache(new MemoryCacheOptions()))
                : null);
        return (authz, view);
    }
}
