using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.Authorization;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Module.Oss;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// End-to-end coverage for EquipmentExt directly located in a Level (#319).
/// The source uses REC hierarchy predicates to verify the materialized SBCO graph used by all
/// runtime queries, rather than testing only hand-written canonical triples.
/// </summary>
public class DirectLevelHierarchyTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    private const string Building = "urn:test:direct-level:building";

    private const string RecDirectLevelTtl = """
        @prefix rec: <https://w3id.org/rec/> .
        @prefix sbco: <https://www.sbco.or.jp/ont/> .

        <urn:test:direct-level:building> a rec:Building ;
          sbco:id "DIRECT-BLDG" ; rec:name "Direct Level Building" ;
          rec:hasPart <urn:test:direct-level:level> .
        <urn:test:direct-level:level> a rec:Level ;
          sbco:id "DIRECT-LVL" ; rec:name "Direct Level" .
        <urn:test:direct-level:equipment> a sbco:EquipmentExt ;
          sbco:id "DIRECT-EQ" ; sbco:name "Direct Level Equipment" ;
          rec:locatedIn <urn:test:direct-level:level> ;
          rec:hasPoint <urn:test:direct-level:point> .
        <urn:test:direct-level:point> a sbco:PointExt ;
          sbco:id "DIRECT-PT" ; sbco:name "Direct Level Point" ;
          sbco:gatewayId "direct-level-gateway" .
        """;

    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private OxiGraphTwinAdminService TwinAdmin() => new(
        oxiGraph.Client,
        new OxiGraphIngestMaterializer(oxiGraph.Client));

    [Fact]
    public async Task RecDirectLevelHierarchy_IsReachableAndReturnedByAllBuildingScopedReads()
    {
        var twinAdmin = TwinAdmin();
        var preview = await twinAdmin.PreviewImportAsync(RecDirectLevelTtl, TwinImportMode.Replace);

        Assert.True(preview.Valid);
        Assert.Equal(0, preview.OrphanCount);

        await twinAdmin.ApplyImportAsync(RecDirectLevelTtl, TwinImportMode.Replace);

        var metadata = await new OxiGraphPointMetadataDataSource(oxiGraph.Client).GetAllAsync();
        var pointMetadata = Assert.Single(metadata, item => item.PointId == "DIRECT-PT");
        Assert.True(pointMetadata.HasBuildingPath);
        Assert.Equal("DIRECT-EQ", pointMetadata.DeviceId);

        var pointAncestors = await new OxiGraphHierarchyResolver(oxiGraph.Client)
            .GetAncestorsAsync("point", "DIRECT-PT");
        Assert.Equal(
            new[] { ("building", "DIRECT-BLDG"), ("floor", "DIRECT-LVL"), ("device", "DIRECT-EQ") },
            pointAncestors);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var database = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);

        var pointDetail = Assert.Single(await database.ListPointDetails(Building));
        Assert.Equal("DIRECT-PT", pointDetail.Point.Id);
        Assert.Equal("Direct Level", pointDetail.Floor?.Name);
        Assert.True(string.IsNullOrEmpty(pointDetail.Space?.Id));

        var deviceDetail = Assert.Single(await database.ListDeviceDetails(Building));
        Assert.Equal("DIRECT-EQ", deviceDetail.Device.Id);
        Assert.Equal("Direct Level", deviceDetail.Floor?.Name);
        Assert.True(string.IsNullOrEmpty(deviceDetail.Space?.Id));

        var hits = await database.SearchResources(null, null, Building, [], 100, 0);
        Assert.Single(hits, hit => hit.Type == "device" && hit.Id == "DIRECT-EQ");
        Assert.Single(hits, hit => hit.Type == "point" && hit.Id == "DIRECT-PT");
    }

    [Fact]
    public async Task DirectLevelAndLegacyFloorLiteral_DoNotDuplicateBuildingScopedResults()
    {
        var turtle = RecDirectLevelTtl.Replace(
            "rec:hasPoint <urn:test:direct-level:point> .",
            "sbco:floor \"Direct Level\" ;\n          rec:hasPoint <urn:test:direct-level:point> .",
            StringComparison.Ordinal);
        await TwinAdmin().ApplyImportAsync(turtle, TwinImportMode.Replace);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var database = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);

        Assert.Single(await database.ListPointDetails(Building));
        Assert.Single(await database.ListDeviceDetails(Building));
        var hits = await database.SearchResources(null, null, Building, [], 100, 0);
        Assert.Single(hits, hit => hit.Type == "device" && hit.Id == "DIRECT-EQ");
        Assert.Single(hits, hit => hit.Type == "point" && hit.Id == "DIRECT-PT");
    }

    // #544: equipment placed directly on a Level is listed under that Level — the only way the tree
    // and /home can reach it, since the floor → room → device walk never passes it.
    [Fact]
    public async Task ListFloorDevices_ReturnsEquipmentDirectlyOnTheLevel_NotInItsRooms()
    {
        const string ttl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <urn:t:b> a sbco:Building ; sbco:id "B" ; sbco:name "B" ; sbco:hasPart <urn:t:l> .
            <urn:t:l> a sbco:Level ; sbco:id "L" ; sbco:name "1F" ; sbco:hasPart <urn:t:r> .
            <urn:t:r> a sbco:Room ; sbco:id "R" ; sbco:name "101" .
            <urn:t:onLevel> a sbco:EquipmentExt ; sbco:id "ON-LEVEL" ; sbco:name "Meter" ;
              sbco:locatedIn <urn:t:l> .
            <urn:t:inRoom> a sbco:EquipmentExt ; sbco:id "IN-ROOM" ; sbco:name "VAV" ;
              sbco:locatedIn <urn:t:r> .
            # A Room is not a Level: asking for a room's "floor devices" returns nothing.
            """;
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var database = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);

        Assert.Equal(["ON-LEVEL"], (await database.ListFloorDevices("urn:t:l")).Select(d => d.Id));
        Assert.Empty(await database.ListFloorDevices("urn:t:r"));
        Assert.Empty(await database.ListFloorDevices("not an iri>"));
    }
}
