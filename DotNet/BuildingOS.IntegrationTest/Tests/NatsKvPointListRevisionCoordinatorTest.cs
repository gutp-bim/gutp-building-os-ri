using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOs.ApiServer.GatewayProvisioning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>Real-NATS verification of the cross-replica Point List revision contract (#260).</summary>
[Collection(Names.Nats)]
public class NatsKvPointListRevisionCoordinatorTest(NatsFixture fixture) : IntegrationTestBase
{
    [Fact]
    public async Task Revision_IsSharedAcrossReplicas_AndInvalidatedForAllReplicas()
    {
        var (connectionA, jsA) = await fixture.CreateJetStreamAsync();
        var (connectionB, jsB) = await fixture.CreateJetStreamAsync();
        await using var disposeA = connectionA;
        await using var disposeB = connectionB;
        var replicaA = new NatsKvPointListRevisionCoordinator(
            jsA, NullLogger<NatsKvPointListRevisionCoordinator>.Instance);
        var replicaB = new NatsKvPointListRevisionCoordinator(
            jsB, NullLogger<NatsKvPointListRevisionCoordinator>.Instance);
        var startupToken = await replicaA.BeginUpdateAsync();
        await replicaA.CompleteUpdateAsync(startupToken);
        var generation = await replicaA.GetGenerationAsync();
        Assert.NotNull(generation);

        await replicaA.SaveIfGenerationUnchangedAsync("GW001", "\"sha256:v1\"", generation!);

        Assert.Equal("\"sha256:v1\"", await replicaB.GetCurrentEtagAsync("GW001"));
        var updateToken = await replicaB.BeginUpdateAsync();
        Assert.Null(await replicaA.GetCurrentEtagAsync("GW001"));
        await Assert.ThrowsAnyAsync<Exception>(() => replicaA.BeginUpdateAsync());
        await replicaB.CompleteUpdateAsync(updateToken);
        Assert.Null(await replicaA.GetCurrentEtagAsync("GW001"));
    }
}
