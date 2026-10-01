using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Module.Oss;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// Integration tests for SBCO TTL idempotent import (issue #106).
/// Verifies: idempotency, PointId queryability after import, and SeedService re-import behaviour.
/// </summary>
public class OxiGraphImportTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    private static readonly string SampleTtlPath = Path.Combine(
        AppContext.BaseDirectory, "Common", "Fixtures", "SeedData", "sbco-sample.ttl");

    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReplaceDefaultGraph_CalledTwice_TripleCountIsStable()
    {
        var ttl = await File.ReadAllTextAsync(SampleTtlPath);

        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);
        var countFirst = await CountTriplesAsync();

        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);
        var countSecond = await CountTriplesAsync();

        Assert.True(countFirst > 0, "should have imported triples");
        Assert.Equal(countFirst, countSecond);
    }

    [Fact]
    public async Task ReplaceDefaultGraph_SbcoTtl_LocalIdsAreQueryable()
    {
        var ttl = await File.ReadAllTextAsync(SampleTtlPath);
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);

        var dataSource = new OxiGraphPointIdDataSource(oxiGraph.Client);
        var infos = await dataSource.GetPointIdInfosAsync();

        Assert.Contains(infos, i => i.Key == "LOCAL005");
    }

    [Fact]
    public async Task SeedService_DataAlreadyPresent_SkipsReimport()
    {
        // 既存データを投入
        var ttl = await File.ReadAllTextAsync(SampleTtlPath);
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);

        // 別内容（PT999 のみ）でシードサービスを再実行
        const string replaceTtl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <https://www.sbco.or.jp/ont/resource/PT999> a sbco:PointExt ;
              sbco:id "PT999" ;
              sbco:localId "LOCAL999" .
            """;

        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, replaceTtl);
            var svc = new OxiGraphSeedHostedService(
                oxiGraph.Client,
                new OxiGraphIngestMaterializer(oxiGraph.Client),
                NullLogger<OxiGraphSeedHostedService>.Instance);
            await svc.RunAsync(tmp, null, CancellationToken.None);
        }
        finally
        {
            File.Delete(tmp);
        }

        var dataSource = new OxiGraphPointIdDataSource(oxiGraph.Client);
        var infos = await dataSource.GetPointIdInfosAsync();

        // #484: RunAsync now gates the import on the store being empty, precisely so a runtime twin
        // (here, standing in for admin-API edits since the last seed) is never silently discarded by
        // a second seed run. A non-empty store must be left untouched — the old content survives and
        // the new file's content never lands.
        Assert.Contains(infos, i => i.Key == "LOCAL005");    // 既存データは残っている
        Assert.DoesNotContain(infos, i => i.Key == "LOCAL999"); // 新データは取り込まれていない
    }

    // Regression for #182: the building-scoped detail query reaches the seed's equipment (placed by
    // sbco:locatedIn) and reports each device's Level.
    [Fact]
    public async Task ListPointDetails_BuildingScoped_ReturnsPointsJoinedByEquipmentFloor()
    {
        const string Bldg1DtId =
            "https://www.sbco.or.jp/ont/resource/building%3Asite%3Asite-1%2Fbldg-1";

        var ttl = await File.ReadAllTextAsync(SampleTtlPath);
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);
        var details = await db.ListPointDetails(Bldg1DtId);

        Assert.NotEmpty(details);
        Assert.All(details, d => Assert.Equal("floor-1", d.Floor!.Name));
        // #183: the seed writes sbco:interval "60" on points; the read path must now surface it as
        // Point.Interval (previously always null because the mapper never projected sbco:interval).
        Assert.Contains(details, d => d.Point.Interval == 60f);
    }

    // #181: gateway_id must belong to a single building; import-time validation must reject a duplicate.
    [Fact]
    public async Task SeedService_GatewayIdSpansMultipleBuildings_Throws()
    {
        const string dupTtl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <https://www.sbco.or.jp/ont/resource/PT001> a sbco:PointExt ;
              sbco:id "PT001" ; sbco:gatewayId "GW001" ; sbco:building "bldg-1" .
            <https://www.sbco.or.jp/ont/resource/PT002> a sbco:PointExt ;
              sbco:id "PT002" ; sbco:gatewayId "GW001" ; sbco:building "bldg-2" .
            """;
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, dupTtl);
            var svc = new OxiGraphSeedHostedService(
                oxiGraph.Client, new OxiGraphIngestMaterializer(oxiGraph.Client), NullLogger<OxiGraphSeedHostedService>.Instance);
            // Import the dup seed, then validate → must throw.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.RunAsync(tmp, null, CancellationToken.None));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task SeedService_GatewayIdsUniquePerBuilding_DoesNotThrow()
    {
        // sbco-sample.ttl: GW001→bldg-1, GW002→bldg-2 (unique per building).
        var svc = new OxiGraphSeedHostedService(
            oxiGraph.Client, new OxiGraphIngestMaterializer(oxiGraph.Client), NullLogger<OxiGraphSeedHostedService>.Instance);

        await svc.RunAsync(SampleTtlPath, null, CancellationToken.None); // must not throw
    }

    // ── #294: single-point detail must not return less than the list ─────────────
    //
    // These run against a real store on purpose. The defect they guard is semantic, not syntactic:
    // a query that omits a variable, or a reachability chain that quietly matches nothing, parses
    // perfectly and returns a row with fields silently absent. Handler-inspecting unit tests can
    // confirm a variable is *requested*; only a real graph confirms it comes back.

    [Fact]
    public async Task GetPoint_ReturnsSpecificationTypeAndGateway()
    {
        var ttl = await File.ReadAllTextAsync(SampleTtlPath);
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);

        var point = await db.GetPoint("PT004");

        Assert.NotNull(point);
        // All three are in the seed; before #294 GetPoint did not SELECT them, so the detail screen
        // rendered "-" while the list screen showed the same point's values correctly.
        Assert.Equal("Measurement", point!.Specification);
        Assert.Equal("CO2 Concentration", point.Type);
        Assert.Equal("GW001", point.GatewayName);
    }

    [Fact]
    public async Task GetPointDetailByPointId_ResolvesBuildingViaSpatialChain()
    {
        var ttl = await File.ReadAllTextAsync(SampleTtlPath);
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);

        var detail = await db.GetPointDetailByPointId("PT004");

        Assert.NotNull(detail);
        // Device.BuildingName had no assignment anywhere in the repository — the field the point
        // detail UI reads was structurally always null.
        Assert.Equal("bldg-1", detail!.Device?.BuildingName);
        // #547: the building as a node (dtId + business id), so a client can scope by it.
        Assert.Equal("https://www.sbco.or.jp/ont/resource/building%3Asite%3Asite-1%2Fbldg-1", detail.Building?.DtId);
        Assert.Equal("building:site:site-1/bldg-1", detail.Building?.Id);
        Assert.Equal("bldg-1", detail.Building?.Name);
    }

    // The floor-literal-only shape: Building → Level, equipment naming the level only through the
    // sbco:floor literal, no sbco:locatedIn. The twin is traversed by topology alone, so the detail
    // resolves the point and its device but places them in no Level or Building — the import's orphan
    // check reports this shape (floor_literal_only) for the builder to fix.
    [Fact]
    public async Task GetPointDetailByPointId_FloorLiteralOnly_IsNotPlaced()
    {
        const string roomlessTtl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <https://www.sbco.or.jp/ont/resource/bldg-example> a sbco:Building ;
              sbco:id "EXAMPLE" ; sbco:name "EXAMPLE" ;
              sbco:hasPart <https://www.sbco.or.jp/ont/resource/level-example-7f> .
            <https://www.sbco.or.jp/ont/resource/level-example-7f> a sbco:Level ;
              sbco:id "7F" ; sbco:name "7F" .
            <https://www.sbco.or.jp/ont/resource/dev-example-1> a sbco:EquipmentExt ;
              sbco:id "172_31_105_17" ; sbco:name "AHU" ;
              sbco:floor "7F" ;
              sbco:hasPoint <https://www.sbco.or.jp/ont/resource/pt-example-3002> .
            <https://www.sbco.or.jp/ont/resource/pt-example-3002> a sbco:PointExt ;
              sbco:id "172_31_105_17-3002" ; sbco:name "On/Off Status" ;
              sbco:pointType "On_Off_Status" ; sbco:pointSpecification "Status" ;
              sbco:writable "false" .
            """;
        await oxiGraph.Client.ReplaceDefaultGraphAsync(roomlessTtl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);

        var detail = await db.GetPointDetailByPointId("172_31_105_17-3002");

        Assert.NotNull(detail);
        Assert.True(string.IsNullOrEmpty(detail!.Device?.BuildingName));
        Assert.Null(detail.Building);
        Assert.True(string.IsNullOrEmpty(detail.Floor?.Name));
        // The point itself still resolves with its metadata.
        Assert.Equal("On_Off_Status", detail.Point.Type);
        Assert.Equal("Status", detail.Point.Specification);
        // No Room in this twin: Space stays blank rather than the query returning nothing at all.
        Assert.True(string.IsNullOrEmpty(detail.Space?.Name));
    }

    [Fact]
    public async Task GetPointDetailByPointId_ResolvesDirectLevelLocationWithoutFloorLiteral()
    {
        const string directLevelTtl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <https://www.sbco.or.jp/ont/resource/bldg-example> a sbco:Building ;
              sbco:id "EXAMPLE" ; sbco:name "EXAMPLE" ;
              sbco:hasPart <https://www.sbco.or.jp/ont/resource/level-example-3f> .
            <https://www.sbco.or.jp/ont/resource/level-example-3f> a sbco:Level ;
              sbco:id "3F" ; sbco:name "3F" .
            <https://www.sbco.or.jp/ont/resource/dev-example-1> a sbco:EquipmentExt ;
              sbco:id "dev-1" ; sbco:name "Light" ;
              sbco:locatedIn <https://www.sbco.or.jp/ont/resource/level-example-3f> ;
              sbco:hasPoint <https://www.sbco.or.jp/ont/resource/pt-example-1> .
            <https://www.sbco.or.jp/ont/resource/pt-example-1> a sbco:PointExt ;
              sbco:id "pt-1" ; sbco:name "Energy" ; sbco:writable "false" .
            """;
        await oxiGraph.Client.ReplaceDefaultGraphAsync(directLevelTtl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);
        var detail = await db.GetPointDetailByPointId("pt-1");

        Assert.NotNull(detail);
        Assert.Equal("EXAMPLE", detail!.Device?.BuildingName);
        Assert.Equal("https://www.sbco.or.jp/ont/resource/bldg-example", detail.Building?.DtId);
        Assert.Equal("EXAMPLE", detail.Building?.Id);
        Assert.Equal("3F", detail.Floor?.Name);
        Assert.True(string.IsNullOrEmpty(detail.Space?.Name));
    }

    // #547 review: equipment placed in a Room of one building AND directly on a Level of another.
    // The detail reports the Room placement (ORDER BY prefers it), so the building must be the one
    // that Room's Level belongs to — derived from the same ?floor, not sampled independently. Both
    // role assignments are run because SAMPLE picks whichever binding the store yields first.
    [Theory]
    [InlineData("A", "B")]
    [InlineData("B", "A")]
    public async Task GetPointDetailByPointId_BuildingFollowsTheReportedPlacement(string roomSide, string levelSide)
    {
        var ttl = $$"""
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <urn:test:bA> a sbco:Building ; sbco:id "BA" ; sbco:name "Building A" ;
              sbco:hasPart <urn:test:lA> .
            <urn:test:lA> a sbco:Level ; sbco:id "LA" ; sbco:name "A-1F" ;
              sbco:hasPart <urn:test:rA> .
            <urn:test:rA> a sbco:Room ; sbco:id "RA" ; sbco:name "A-101" .
            <urn:test:bB> a sbco:Building ; sbco:id "BB" ; sbco:name "Building B" ;
              sbco:hasPart <urn:test:lB> .
            <urn:test:lB> a sbco:Level ; sbco:id "LB" ; sbco:name "B-1F" ;
              sbco:hasPart <urn:test:rB> .
            <urn:test:rB> a sbco:Room ; sbco:id "RB" ; sbco:name "B-101" .
            <urn:test:dev> a sbco:EquipmentExt ; sbco:id "DEV-X" ; sbco:name "Shared" ;
              sbco:locatedIn <urn:test:r{{roomSide}}> , <urn:test:l{{levelSide}}> ;
              sbco:hasPoint <urn:test:pt> .
            <urn:test:pt> a sbco:PointExt ; sbco:id "PT-X" ; sbco:name "X" ; sbco:writable "false" .
            """;
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);
        var detail = await db.GetPointDetailByPointId("PT-X");

        Assert.NotNull(detail);
        Assert.Equal($"R{roomSide}", detail!.Space?.Id);
        Assert.Equal($"L{roomSide}", detail.Floor?.Id);
        Assert.Equal($"urn:test:b{roomSide}", detail.Building?.DtId);
        Assert.Equal($"B{roomSide}", detail.Building?.Id);
        Assert.Equal($"Building {roomSide}", detail.Building?.Name);
        Assert.Equal($"Building {roomSide}", detail.Device?.BuildingName);
    }

    [Fact]
    public async Task ListPointDetails_ReportsTheBuildingItWasQueriedFor()
    {
        const string Bldg1DtId =
            "https://www.sbco.or.jp/ont/resource/building%3Asite%3Asite-1%2Fbldg-1";

        var ttl = await File.ReadAllTextAsync(SampleTtlPath);
        await oxiGraph.Client.ReplaceDefaultGraphAsync(ttl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);
        var details = await db.ListPointDetails(Bldg1DtId);

        Assert.NotEmpty(details);
        // List and detail must agree about which building a point is in (#294).
        Assert.All(details, d => Assert.Equal("bldg-1", d.Device?.BuildingName));
        Assert.All(details, d =>
        {
            Assert.Equal(Bldg1DtId, d.Building?.DtId);
            Assert.Equal("building:site:site-1/bldg-1", d.Building?.Id);
            Assert.Equal("bldg-1", d.Building?.Name);
        });
    }

    private async Task<int> CountTriplesAsync()
    {
        var rows = await oxiGraph.Client.QueryAsync(
            "SELECT (COUNT(*) AS ?c) WHERE { ?s ?p ?o }");
        return int.Parse(rows[0]["c"]);
    }
}
