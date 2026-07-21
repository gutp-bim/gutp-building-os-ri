using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// Real-NATS coverage for the gateway connection heartbeat (#230/ADR-0004) — the piece the unit tests
/// (which use a fake store) can't reach: the KV round-trip, the epoch-guarded teardown, and the
/// bucket-level MaxAge TTL that expires an entry a crashed replica never deleted.
/// </summary>
[Collection(Names.Nats)]
public class NatsKvGatewayConnectionStoreTest(NatsFixture fixture) : IntegrationTestBase
{
    private async Task<NatsKvGatewayConnectionStore> CreateStoreAsync(
        TimeSpan? ttl = null,
        string? bucketName = null)
    {
        var (_, js) = await fixture.CreateJetStreamAsync();
        return bucketName is null
            ? new NatsKvGatewayConnectionStore(js, NullLogger<NatsKvGatewayConnectionStore>.Instance, ttl)
            : new NatsKvGatewayConnectionStore(
                js, NullLogger<NatsKvGatewayConnectionStore>.Instance, ttl, bucketName);
    }

    [Fact]
    public async Task MarkConnected_Then_Get_Returns_Status()
    {
        var store = await CreateStoreAsync();
        var gatewayId = $"gw-{Guid.NewGuid():N}";

        await store.MarkConnectedAsync(gatewayId, "replica-a");
        var status = await store.GetAsync(gatewayId);

        Assert.NotNull(status);
        Assert.Equal("replica-a", status!.ReplicaId);
        Assert.Null(status.AppliedRevision); // none reported yet (#230 Phase 2b)
    }

    [Fact]
    public async Task MarkConnected_WithAppliedRevision_RoundTrips()
    {
        // #230 Phase 2b: the gateway's applied point-list ETag is persisted on the heartbeat entry so
        // the admin read side can derive pointlist sync state.
        var store = await CreateStoreAsync();
        var gatewayId = $"gw-{Guid.NewGuid():N}";

        await store.MarkConnectedAsync(gatewayId, "replica-a", "\"sha256:abc\"");
        var status = await store.GetAsync(gatewayId);

        Assert.NotNull(status);
        Assert.Equal("\"sha256:abc\"", status!.AppliedRevision);
    }

    [Fact]
    public async Task Get_Returns_Null_WhenGatewayNeverConnected()
    {
        var store = await CreateStoreAsync();
        Assert.Null(await store.GetAsync($"gw-{Guid.NewGuid():N}"));
    }

    [Fact]
    public async Task MarkDisconnected_ByOwningReplica_Clears_Entry()
    {
        var store = await CreateStoreAsync();
        var gatewayId = $"gw-{Guid.NewGuid():N}";

        await store.MarkConnectedAsync(gatewayId, "replica-a");
        await store.MarkDisconnectedAsync(gatewayId, "replica-a");

        Assert.Null(await store.GetAsync(gatewayId));
    }

    [Fact]
    public async Task MarkDisconnected_ByOtherReplica_LeavesEntry_EpochGuard()
    {
        // A stream that moved to replica-b must not be torn down by replica-a's late teardown.
        var store = await CreateStoreAsync();
        var gatewayId = $"gw-{Guid.NewGuid():N}";

        await store.MarkConnectedAsync(gatewayId, "replica-b");
        await store.MarkDisconnectedAsync(gatewayId, "replica-a"); // stale owner — must be a no-op

        var status = await store.GetAsync(gatewayId);
        Assert.NotNull(status);
        Assert.Equal("replica-b", status!.ReplicaId);
    }

    [Fact]
    public async Task Entry_Expires_After_Ttl()
    {
        // The TTL backstop: a heartbeat a crashed replica never refreshed disappears on its own.
        // Use an isolated bucket because the other tests share the production bucket with its default
        // TTL. Re-creating that bucket with a different MaxAge is rejected by JetStream.
        var store = await CreateStoreAsync(TimeSpan.FromSeconds(1), $"gateway-ttl-{Guid.NewGuid():N}");
        var gatewayId = $"gw-{Guid.NewGuid():N}";

        await store.MarkConnectedAsync(gatewayId, "replica-a");
        Assert.NotNull(await store.GetAsync(gatewayId));

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Null(await store.GetAsync(gatewayId));
    }
}
