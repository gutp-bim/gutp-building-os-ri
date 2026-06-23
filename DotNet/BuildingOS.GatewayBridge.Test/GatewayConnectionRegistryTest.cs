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
}
