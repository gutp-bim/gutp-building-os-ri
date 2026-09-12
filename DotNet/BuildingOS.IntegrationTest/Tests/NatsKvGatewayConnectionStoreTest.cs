using BuildingOS.Shared.Domain.Types;
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
        var lookup = await store.GetAsync(gatewayId);

        Assert.Equal(GatewayConnectionState.Connected, lookup.State);
        Assert.Equal("replica-a", lookup.Status!.ReplicaId);
        Assert.Null(lookup.Status.AppliedRevision); // none reported yet (#230 Phase 2b)
    }

    [Fact]
    public async Task MarkConnected_WithAppliedRevision_RoundTrips()
    {
        // #230 Phase 2b: the gateway's applied point-list ETag is persisted on the heartbeat entry so
        // the admin read side can derive pointlist sync state.
        var store = await CreateStoreAsync();
        var gatewayId = $"gw-{Guid.NewGuid():N}";

        await store.MarkConnectedAsync(gatewayId, "replica-a", "\"sha256:abc\"");
        var lookup = await store.GetAsync(gatewayId);

        Assert.Equal(GatewayConnectionState.Connected, lookup.State);
        Assert.Equal("\"sha256:abc\"", lookup.Status!.AppliedRevision);
    }

    [Fact]
    public async Task Get_IsDisconnected_WhenGatewayNeverConnected()
    {
        // The store answered and found nothing — that is 未接続, not 不明 (#463).
        var store = await CreateStoreAsync();

        var lookup = await store.GetAsync($"gw-{Guid.NewGuid():N}");

        Assert.Equal(GatewayConnectionState.Disconnected, lookup.State);
        Assert.Null(lookup.Status);
    }

    [Fact]
    public async Task MarkDisconnected_ByOwningReplica_Clears_Entry()
    {
        var store = await CreateStoreAsync();
        var gatewayId = $"gw-{Guid.NewGuid():N}";

        await store.MarkConnectedAsync(gatewayId, "replica-a");
        await store.MarkDisconnectedAsync(gatewayId, "replica-a");

        Assert.Equal(GatewayConnectionState.Disconnected, (await store.GetAsync(gatewayId)).State);
    }

    [Fact]
    public async Task MarkDisconnected_ByOtherReplica_LeavesEntry_EpochGuard()
    {
        // A stream that moved to replica-b must not be torn down by replica-a's late teardown.
        var store = await CreateStoreAsync();
        var gatewayId = $"gw-{Guid.NewGuid():N}";

        await store.MarkConnectedAsync(gatewayId, "replica-b");
        await store.MarkDisconnectedAsync(gatewayId, "replica-a"); // stale owner — must be a no-op

        var lookup = await store.GetAsync(gatewayId);
        Assert.Equal(GatewayConnectionState.Connected, lookup.State);
        Assert.Equal("replica-b", lookup.Status!.ReplicaId);
    }

    [Fact]
    public async Task MultipleGateways_TrackedIndependently_DisconnectingOneLeavesOtherUntouched()
    {
        // Two distinct gateway_ids sharing the same KV bucket: proves MarkConnected/MarkDisconnected/Get
        // are correctly scoped per gatewayId and never cross-contaminate (#114 follow-up gap — every
        // existing test above uses a single gatewayId).
        var store = await CreateStoreAsync();
        var gwA = $"gw-{Guid.NewGuid():N}";
        var gwB = $"gw-{Guid.NewGuid():N}";

        await store.MarkConnectedAsync(gwA, "replica-a");
        await store.MarkConnectedAsync(gwB, "replica-b");

        var lookupA = await store.GetAsync(gwA);
        var lookupB = await store.GetAsync(gwB);
        Assert.Equal("replica-a", lookupA.Status!.ReplicaId);
        Assert.Equal("replica-b", lookupB.Status!.ReplicaId);

        await store.MarkDisconnectedAsync(gwA, "replica-a");

        Assert.Equal(GatewayConnectionState.Disconnected, (await store.GetAsync(gwA)).State);
        var stillB = await store.GetAsync(gwB);
        Assert.Equal(GatewayConnectionState.Connected, stillB.State);
        Assert.Equal("replica-b", stillB.Status!.ReplicaId);
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
        Assert.Equal(GatewayConnectionState.Connected, (await store.GetAsync(gatewayId)).State);

        await Task.Delay(TimeSpan.FromSeconds(2));
        // TTL 切れは「観測上つながっていない」。読めなかった (Unknown) とは区別する。
        Assert.Equal(GatewayConnectionState.Disconnected, (await store.GetAsync(gatewayId)).State);
    }

    /// <summary>
    /// #463: KV がそもそも読めないとき、「未接続」ではなく「不明」を返す。ここが Disconnected に
    /// 丸まっていたせいで、NATS の不調のたびに `/health` の欠測が全件ゲートウェイ切断に見えていた。
    /// 接続を落として実際に読めない状態を作る（例外を投げないことも同時に確かめる）。
    /// </summary>
    [Fact]
    public async Task Get_WhenNatsIsUnreachable_IsUnknown_NotDisconnected()
    {
        var (nats, js) = await fixture.CreateJetStreamAsync();
        var store = new NatsKvGatewayConnectionStore(
            js, NullLogger<NatsKvGatewayConnectionStore>.Instance);
        var gatewayId = $"gw-{Guid.NewGuid():N}";

        await store.MarkConnectedAsync(gatewayId, "replica-a");
        Assert.Equal(GatewayConnectionState.Connected, (await store.GetAsync(gatewayId)).State);

        await nats.DisposeAsync();

        var lookup = await store.GetAsync(gatewayId);

        Assert.Equal(GatewayConnectionState.Unknown, lookup.State);
        Assert.Null(lookup.Status);
    }
}
