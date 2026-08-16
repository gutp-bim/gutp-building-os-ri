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
    public async Task SeedService_DataAlreadyPresent_ReimportsWithNewContent()
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

        Assert.DoesNotContain(infos, i => i.Key == "LOCAL005"); // 旧データは消えている
        Assert.Contains(infos, i => i.Key == "LOCAL999");       // 新データが存在する
    }

    // Regression for #182: building-scoped detail queries join building→equipment via sbco:floor
    // asserted on EquipmentExt (OxiGraphDigitalTwinDatabase.ListPointDetails). If sbco:floor lives
    // only on PointExt (the original seed bug), this non-OPTIONAL join yields zero rows.
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
    }

    // The THX shape: Building → Level, equipment joined to the level by the sbco:floor literal, and
    // no Room anywhere. This repository treats Room/locatedIn as optional (ListPointDetails already
    // joins through the literal), so requiring the spatial chain would leave BuildingName null for
    // every twin modelled this way — which is most of them.
    [Fact]
    public async Task GetPointDetailByPointId_ResolvesBuildingWithoutRooms()
    {
        const string roomlessTtl = """
            @prefix sbco: <https://www.sbco.or.jp/ont/> .
            <https://www.sbco.or.jp/ont/resource/bldg-thx> a sbco:Building ;
              sbco:id "THX" ; sbco:name "THX" ;
              sbco:hasPart <https://www.sbco.or.jp/ont/resource/level-thx-7f> .
            <https://www.sbco.or.jp/ont/resource/level-thx-7f> a sbco:Level ;
              sbco:id "7F" ; sbco:name "7F" .
            <https://www.sbco.or.jp/ont/resource/dev-thx-1> a sbco:EquipmentExt ;
              sbco:id "172_31_105_17" ; sbco:name "AHU" ;
              sbco:floor "7F" ;
              sbco:hasPoint <https://www.sbco.or.jp/ont/resource/pt-thx-3002> .
            <https://www.sbco.or.jp/ont/resource/pt-thx-3002> a sbco:PointExt ;
              sbco:id "172_31_105_17-3002" ; sbco:name "On/Off Status" ;
              sbco:pointType "On_Off_Status" ; sbco:pointSpecification "Status" ;
              sbco:writable "false" .
            """;
        await oxiGraph.Client.ReplaceDefaultGraphAsync(roomlessTtl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);

        var detail = await db.GetPointDetailByPointId("172_31_105_17-3002");

        Assert.NotNull(detail);
        Assert.Equal("THX", detail!.Device?.BuildingName);
        Assert.Equal("7F", detail.Floor?.Name);
        // The same point's type/specification must survive the detail path too — this is the exact
        // point from the THX report.
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
            <https://www.sbco.or.jp/ont/resource/bldg-thx> a sbco:Building ;
              sbco:id "THX" ; sbco:name "THX" ;
              sbco:hasPart <https://www.sbco.or.jp/ont/resource/level-thx-3f> .
            <https://www.sbco.or.jp/ont/resource/level-thx-3f> a sbco:Level ;
              sbco:id "3F" ; sbco:name "3F" .
            <https://www.sbco.or.jp/ont/resource/dev-thx-1> a sbco:EquipmentExt ;
              sbco:id "dev-1" ; sbco:name "Light" ;
              sbco:locatedIn <https://www.sbco.or.jp/ont/resource/level-thx-3f> ;
              sbco:hasPoint <https://www.sbco.or.jp/ont/resource/pt-thx-1> .
            <https://www.sbco.or.jp/ont/resource/pt-thx-1> a sbco:PointExt ;
              sbco:id "pt-1" ; sbco:name "Energy" ; sbco:writable "false" .
            """;
        await oxiGraph.Client.ReplaceDefaultGraphAsync(directLevelTtl);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(oxiGraph.Client, cache);
        var detail = await db.GetPointDetailByPointId("pt-1");

        Assert.NotNull(detail);
        Assert.Equal("THX", detail!.Device?.BuildingName);
        Assert.Equal("3F", detail.Floor?.Name);
        Assert.True(string.IsNullOrEmpty(detail.Space?.Name));
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
    }

    private async Task<int> CountTriplesAsync()
    {
        var rows = await oxiGraph.Client.QueryAsync(
            "SELECT (COUNT(*) AS ?c) WHERE { ?s ?p ?o }");
        return int.Parse(rows[0]["c"]);
    }
}
