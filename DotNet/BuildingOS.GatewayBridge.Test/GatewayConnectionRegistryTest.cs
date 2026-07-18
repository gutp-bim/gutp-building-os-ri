using BuildingOS.GatewayBridge.Infrastructure;

namespace BuildingOS.GatewayBridge.Test;

public class GatewayConnectionRegistryTest
{
    [Fact]
    public void Register_TracksConnectedGateway()
    {
        var registry = new GatewayConnectionRegistry();
        var connection = registry.Register("gw-1");

        Assert.False(connection.IsSuperseded);
        Assert.True(registry.IsConnected("gw-1"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Register_Again_SupersedesPreviousConnection()
    {
        // Supersede semantics (multi-connection): a second connection for the same gateway on this
        // replica is accepted and the previous one is signalled to tear down — reconnecting after a
        // half-open drop must not lock the gateway out (the old AlreadyExists behaviour).
        var registry = new GatewayConnectionRegistry();
        var first = registry.Register("gw-1");
        var second = registry.Register("gw-1");

        Assert.True(first.IsSuperseded);
        Assert.True(first.SupersededToken.IsCancellationRequested);
        Assert.False(second.IsSuperseded);
        Assert.Equal(1, registry.Count); // still one active connection per gateway
    }

    [Fact]
    public void Unregister_RemovesGateway()
    {
        var registry = new GatewayConnectionRegistry();
        var connection = registry.Register("gw-1");

        registry.Unregister(connection);

        Assert.False(registry.IsConnected("gw-1"));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Unregister_SupersededConnection_DoesNotEvictTheNewerOne()
    {
        // Epoch guard: when the superseded connection finally tears down, its Unregister must not
        // remove the newer connection that has already taken over the slot.
        var registry = new GatewayConnectionRegistry();
        var first = registry.Register("gw-1");
        var second = registry.Register("gw-1"); // supersedes first

        registry.Unregister(first); // late teardown of the old stream

        Assert.True(registry.IsConnected("gw-1")); // second still owns the slot
        Assert.Equal(1, registry.Count);
        Assert.False(second.IsSuperseded);

        registry.Unregister(second);
        Assert.False(registry.IsConnected("gw-1"));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Register_ConcurrentAttempts_SameGatewayId_ConvergeToOneActive()
    {
        // #114: many simultaneous connect attempts for the same gateway id on one replica (a
        // reconnect race) all succeed under supersede, but exactly one connection stays active and
        // every other is superseded.
        var registry = new GatewayConnectionRegistry();
        const int attempts = 50;

        var connections = await Task.WhenAll(
            Enumerable.Range(0, attempts).Select(_ => Task.Run(() => registry.Register("gw-race"))));

        Assert.Equal(1, registry.Count);
        Assert.Equal(1, connections.Count(c => !c.IsSuperseded)); // one winner
        Assert.Equal(attempts - 1, connections.Count(c => c.IsSuperseded)); // rest superseded
    }

    [Fact]
    public void Register_DistinctGatewayIds_AllTrackedIndependently()
    {
        var registry = new GatewayConnectionRegistry();
        var gatewayIds = Enumerable.Range(1, 20).Select(i => $"gw-{i}").ToArray();

        var connections = gatewayIds.Select(registry.Register).ToArray();

        Assert.Equal(gatewayIds.Length, registry.Count);
        Assert.All(connections, c => Assert.False(c.IsSuperseded));
        Assert.All(gatewayIds, id => Assert.True(registry.IsConnected(id)));
    }
}
