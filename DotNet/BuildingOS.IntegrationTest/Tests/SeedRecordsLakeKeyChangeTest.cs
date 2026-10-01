using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #527: seeding a twin (into an empty store) may place points under different buildings than the
/// twin the lake was written with, so it records a lake partition-key change; a store that is not
/// empty is not seeded and records nothing.
/// </summary>
public class SeedRecordsLakeKeyChangeTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Ttl = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        <urn:test:bldg-1> a sbco:Building ; sbco:id "bldg-1" ; sbco:name "Building 1" .
        """;

    private async Task<Mock<ILakePartitionKeyChanges>> SeedAsync()
    {
        var lake = new Mock<ILakePartitionKeyChanges>();
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, Ttl);
            var svc = new OxiGraphSeedHostedService(
                oxiGraph.Client, new OxiGraphIngestMaterializer(oxiGraph.Client),
                NullLogger<OxiGraphSeedHostedService>.Instance, lakeKeys: lake.Object);
            await svc.RunAsync(tmp, null, CancellationToken.None);
        }
        finally
        {
            File.Delete(tmp);
        }
        return lake;
    }

    [Fact]
    public async Task SeedingAnEmptyStore_RecordsAKeyChange()
    {
        var lake = await SeedAsync();

        lake.Verify(l => l.MarkChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ANonEmptyStore_IsNotSeeded_AndRecordsNothing()
    {
        await oxiGraph.Client.ReplaceDefaultGraphAsync(Ttl);

        var lake = await SeedAsync();

        lake.Verify(l => l.MarkChangedAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
