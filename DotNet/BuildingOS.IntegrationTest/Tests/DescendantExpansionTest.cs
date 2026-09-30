using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Grouping.Entities;
using BuildingOS.Shared.Infrastructure.Authorization;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #509 against a real twin: descendant expansion follows the same paths as the ancestor chain, so
/// every expanded id is readable through CanAccessAsync and every node it leaves out is not. The twin
/// covers each device placement the ancestor query accepts (in a room, directly on a level), a device
/// placed both ways, equipment named only by the sbco:floor literal (placed nowhere — traversal is
/// topology only), a room with no level, and a second building with a same-named floor.
/// </summary>
[Collection(Names.Postgres)]
public class DescendantExpansionTest(PostgresFixture postgres, OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    private const string Ns = "https://www.sbco.or.jp/ont/resource/it509-";
    private static string Iri(string id) => Ns + id;

    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task SeedAsync() => oxiGraph.Client.UpdateAsync($$"""
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        INSERT DATA {
          <{{Iri("B1")}}> a sbco:Building ; sbco:id "B1" ; sbco:name "Tower" ; sbco:hasPart <{{Iri("F5")}}> , <{{Iri("F6")}}> .
          <{{Iri("F5")}}> a sbco:Level ; sbco:id "F5" ; sbco:name "5F" ; sbco:hasPart <{{Iri("R501")}}> , <{{Iri("R502")}}> .
          <{{Iri("F6")}}> a sbco:Level ; sbco:id "F6" ; sbco:name "6F" .
          <{{Iri("R501")}}> a sbco:Room ; sbco:id "R501" ; sbco:name "501" .
          <{{Iri("R502")}}> a sbco:Room ; sbco:id "R502" ; sbco:name "502" .
          <{{Iri("R-ORPHAN")}}> a sbco:Room ; sbco:id "R-ORPHAN" ; sbco:name "orphan" .
          <{{Iri("AHU-501")}}> a sbco:EquipmentExt ; sbco:id "AHU-501" ; sbco:name "a" ; sbco:locatedIn <{{Iri("R501")}}> ; sbco:hasPoint <{{Iri("PT-1")}}> , <{{Iri("PT-2")}}> .
          <{{Iri("AHU-502")}}> a sbco:EquipmentExt ; sbco:id "AHU-502" ; sbco:name "b" ; sbco:locatedIn <{{Iri("R502")}}> ; sbco:hasPoint <{{Iri("PT-3")}}> .
          <{{Iri("PUMP-5F")}}> a sbco:EquipmentExt ; sbco:id "PUMP-5F" ; sbco:name "c" ; sbco:locatedIn <{{Iri("F5")}}> ; sbco:hasPoint <{{Iri("PT-4")}}> .
          <{{Iri("FAN-6F")}}> a sbco:EquipmentExt ; sbco:id "FAN-6F" ; sbco:name "d" ; sbco:floor "6F" ; sbco:hasPoint <{{Iri("PT-5")}}> .
          <{{Iri("DUAL")}}> a sbco:EquipmentExt ; sbco:id "DUAL" ; sbco:name "f" ; sbco:locatedIn <{{Iri("R502")}}> , <{{Iri("F6")}}> ; sbco:hasPoint <{{Iri("PT-7")}}> .
          <{{Iri("PT-7")}}> a sbco:PointExt ; sbco:id "PT-7" ; sbco:name "p7" .
          <{{Iri("B2")}}> a sbco:Building ; sbco:id "B2" ; sbco:name "Annex" ; sbco:hasPart <{{Iri("B2-F5")}}> .
          <{{Iri("B2-F5")}}> a sbco:Level ; sbco:id "B2-F5" ; sbco:name "5F" .
          <{{Iri("B2-AHU")}}> a sbco:EquipmentExt ; sbco:id "B2-AHU" ; sbco:name "g" ; sbco:locatedIn <{{Iri("B2-F5")}}> ; sbco:floor "5F" ; sbco:hasPoint <{{Iri("PT-8")}}> .
          <{{Iri("PT-8")}}> a sbco:PointExt ; sbco:id "PT-8" ; sbco:name "p8" .
          <{{Iri("LOST")}}> a sbco:EquipmentExt ; sbco:id "LOST" ; sbco:name "e" ; sbco:locatedIn <{{Iri("R-ORPHAN")}}> ; sbco:hasPoint <{{Iri("PT-6")}}> .
          <{{Iri("PT-1")}}> a sbco:PointExt ; sbco:id "PT-1" ; sbco:name "p1" .
          <{{Iri("PT-2")}}> a sbco:PointExt ; sbco:id "PT-2" ; sbco:name "p2" .
          <{{Iri("PT-3")}}> a sbco:PointExt ; sbco:id "PT-3" ; sbco:name "p3" .
          <{{Iri("PT-4")}}> a sbco:PointExt ; sbco:id "PT-4" ; sbco:name "p4" .
          <{{Iri("PT-5")}}> a sbco:PointExt ; sbco:id "PT-5" ; sbco:name "p5" .
          <{{Iri("PT-6")}}> a sbco:PointExt ; sbco:id "PT-6" ; sbco:name "p6" .
        }
        """);

    private static readonly (string Type, string Id)[] AllNodes =
    [
        ("building", "B1"), ("building", "B2"), ("floor", "F5"), ("floor", "F6"), ("floor", "B2-F5"),
        ("space", "R501"), ("space", "R502"), ("space", "R-ORPHAN"),
        ("device", "AHU-501"), ("device", "AHU-502"), ("device", "PUMP-5F"), ("device", "FAN-6F"), ("device", "LOST"),
        ("device", "DUAL"), ("device", "B2-AHU"),
        ("point", "PT-1"), ("point", "PT-2"), ("point", "PT-3"), ("point", "PT-4"), ("point", "PT-5"), ("point", "PT-6"),
        ("point", "PT-7"), ("point", "PT-8"),
    ];

    public static TheoryData<string, string, string[]> Cases() => new()
    {
        { "space", "R501", ["device:AHU-501", "point:PT-1", "point:PT-2"] },
        { "space", "R502", ["device:AHU-502", "device:DUAL", "point:PT-3", "point:PT-7"] },
        { "floor", "F5", ["space:R501", "space:R502", "device:AHU-501", "device:AHU-502", "device:PUMP-5F", "device:DUAL",
                          "point:PT-1", "point:PT-2", "point:PT-3", "point:PT-4", "point:PT-7"] },
        // FAN-6F names 6F only through the literal: placed nowhere. DUAL is directly on 6F too.
        { "floor", "F6", ["device:DUAL", "point:PT-7"] },
        { "building", "B1", ["floor:F5", "floor:F6", "space:R501", "space:R502",
                             "device:AHU-501", "device:AHU-502", "device:PUMP-5F", "device:DUAL",
                             "point:PT-1", "point:PT-2", "point:PT-3", "point:PT-4", "point:PT-7"] },
        // B2's floor is also named "5F": no name join, so nothing of B1 leaks in (and vice versa above).
        { "building", "B2", ["floor:B2-F5", "device:B2-AHU", "point:PT-8"] },
        { "device", "AHU-502", ["point:PT-3"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Descendants_MatchWhatTheAncestorChainLetsTheGrantReach(string rootType, string rootId, string[] expected)
    {
        await SeedAsync();
        var descendants = await new OxiGraphDescendantResolver(oxiGraph.Client)
            .GetDescendantsAsync([(rootType, rootId)], "point");
        var got = descendants.SelectMany(kv => kv.Value.Select(id => $"{kv.Key}:{id}")).OrderBy(x => x).ToArray();
        Assert.Equal(expected.OrderBy(x => x).ToArray(), got);

        // Parity with authorization: each expanded node is readable under the root's grant, and every
        // other node below the root's type is not (the room with no level stays unreachable).
        await using var db = new RelationalDbContext(new DbContextOptionsBuilder<RelationalDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        await db.Database.MigrateAsync();
        var authz = new DefaultAuthorizationService(
            new GroupMembershipResolver(new GroupRepository(db, NullLogger<GroupRepository>.Instance)),
            new OxiGraphHierarchyResolver(oxiGraph.Client),
            NullLogger<DefaultAuthorizationService>.Instance);
        var user = new AuthorizationContext
        {
            UserId = "u", Role = "viewer", Permissions = [PermissionHelper.BuildPermissionString(rootType, rootId, "read")],
        };
        var rootRank = IResourceDescendantResolver.Types.ToList().IndexOf(rootType);
        foreach (var (type, id) in AllNodes.Where(n => IResourceDescendantResolver.Types.ToList().IndexOf(n.Type) > rootRank))
        {
            var readable = await authz.CanAccessAsync(user, type, id, "read");
            Assert.True(readable == got.Contains($"{type}:{id}"), $"{type}:{id} readable={readable} but expanded={got.Contains($"{type}:{id}")}");
        }
    }

    [Fact]
    public async Task TargetType_StopsTheExpansion()
    {
        await SeedAsync();
        var descendants = await new OxiGraphDescendantResolver(oxiGraph.Client)
            .GetDescendantsAsync([("building", "B1")], "space");

        Assert.Equal(["floor", "space"], descendants.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).OrderBy(x => x));
    }

    [Fact]
    public async Task MyResources_ExpandDescendants_ForAGroupGrantOnARoom()
    {
        // The Tenant Portal case (#509): one call answers "which points can this user read".
        await SeedAsync();
        await using var db = new RelationalDbContext(new DbContextOptionsBuilder<RelationalDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        await db.Database.MigrateAsync();
        var groupId = $"tenant-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        db.ResourceGroups.Add(new ResourceGroup
        {
            Id = groupId, Name = "tenant", CreatedAt = now, UpdatedAt = now,
            ResourceItems = [new GroupResourceItem { Id = Guid.NewGuid().ToString("N"), ResourceType = "space", ResourceId = "R501", CreatedAt = now }],
        });
        await db.SaveChangesAsync();

        var authz = new DefaultAuthorizationService(
            new GroupMembershipResolver(new GroupRepository(db, NullLogger<GroupRepository>.Instance)),
            new OxiGraphHierarchyResolver(oxiGraph.Client),
            NullLogger<DefaultAuthorizationService>.Instance);
        var controller = new MyResourcesController(authz, new ResourceIdMappingRepository(db), new OxiGraphDescendantResolver(oxiGraph.Client))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext
                    {
                        UserId = "u-office-a", Role = "viewer",
                        Permissions = [PermissionHelper.BuildPermissionString("group", groupId, "read")],
                    } },
                },
            },
        };

        var ok = Assert.IsType<OkObjectResult>(
            await controller.GetMyResources(idFormat: "original", expand: "descendants"));
        var body = Assert.IsType<MyResourcesResponse>(ok.Value);

        Assert.Equal(["R501"], body.Resources!["space"]);
        Assert.Equal(["AHU-501"], body.Resources["device"]);
        Assert.Equal(["PT-1", "PT-2"], body.Resources["point"].OrderBy(x => x));
        Assert.Empty(body.Resources["building"]);
        foreach (var pointId in body.Resources["point"])
            Assert.True(await authz.CanAccessAsync(controller.HttpContext.GetAuthorizationContext(), "point", pointId, "read"));
    }

    [Fact]
    public async Task MaxIds_StopsAnExpansionThatWouldReturnMore()
    {
        // B1 expands to 13 ids; with a limit of 5 the resolver must refuse rather than load them all.
        await SeedAsync();
        var resolver = new OxiGraphDescendantResolver(oxiGraph.Client);

        var ex = await Assert.ThrowsAsync<DescendantLimitExceededException>(
            () => resolver.GetDescendantsAsync([("building", "B1")], "point", maxIds: 5));
        Assert.Equal(5, ex.Limit);

        // At or under the limit the answer is complete.
        var within = await resolver.GetDescendantsAsync([("device", "AHU-501")], "point", maxIds: 2);
        Assert.Equal(["PT-1", "PT-2"], within["point"]);
    }
}
