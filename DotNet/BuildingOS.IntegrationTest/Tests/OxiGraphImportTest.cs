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
                oxiGraph.Client, NullLogger<OxiGraphSeedHostedService>.Instance);
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
            oxiGraph.Client, NullLogger<OxiGraphSeedHostedService>.Instance);

        await svc.RunAsync(SampleTtlPath, null, CancellationToken.None); // must not throw
    }

    private async Task<int> CountTriplesAsync()
    {
        var rows = await oxiGraph.Client.QueryAsync(
            "SELECT (COUNT(*) AS ?c) WHERE { ?s ?p ?o }");
        return int.Parse(rows[0]["c"]);
    }
}
