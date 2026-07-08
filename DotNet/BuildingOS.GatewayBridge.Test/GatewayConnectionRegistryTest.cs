using BuildingOS.GatewayBridge.Infrastructure;

namespace BuildingOS.GatewayBridge.Test;

public class GatewayConnectionRegistryTest
{
    [Fact]
    public void TryRegister_TracksConnectedGateway()
    {
        var registry = new GatewayConnectionRegistry();
        Assert.True(registry.TryRegister("gw-1"));
        Assert.True(registry.IsConnected("gw-1"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void TryRegister_RejectsDuplicateOnSameReplica()
    {
        var registry = new GatewayConnectionRegistry();
        registry.TryRegister("gw-1");
        Assert.False(registry.TryRegister("gw-1"));
    }

    [Fact]
    public void Unregister_RemovesGateway()
    {
        var registry = new GatewayConnectionRegistry();
        registry.TryRegister("gw-1");
        registry.Unregister("gw-1");
        Assert.False(registry.IsConnected("gw-1"));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task TryRegister_ConcurrentAttempts_SameGatewayId_OnlyOneSucceeds()
    {
        // #114: many simultaneous connect attempts for the same gateway id on one replica (a
        // reconnect race) must not double-register — exactly one wins.
        var registry = new GatewayConnectionRegistry();
        const int attempts = 50;
        var results = await Task.WhenAll(
            Enumerable.Range(0, attempts).Select(_ => Task.Run(() => registry.TryRegister("gw-race"))));

        Assert.Equal(1, results.Count(succeeded => succeeded));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void TryRegister_DistinctGatewayIds_AllSucceedIndependently()
    {
        var registry = new GatewayConnectionRegistry();
        var gatewayIds = Enumerable.Range(1, 20).Select(i => $"gw-{i}").ToArray();

        Assert.All(gatewayIds, id => Assert.True(registry.TryRegister(id)));
        Assert.Equal(gatewayIds.Length, registry.Count);
        Assert.All(gatewayIds, id => Assert.True(registry.IsConnected(id)));
    }
}
