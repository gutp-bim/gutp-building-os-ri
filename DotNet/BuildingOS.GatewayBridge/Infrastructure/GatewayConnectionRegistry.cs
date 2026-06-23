using System.Collections.Concurrent;

namespace BuildingOS.GatewayBridge.Infrastructure;

/// <summary>
/// In-memory registry of gateways currently connected to THIS bridge replica. There is no
/// persistent state (plan §3-3): on pod restart the registry is rebuilt as gateways reconnect.
/// Used for observability and to enforce one active egress stream per gateway on a replica.
/// </summary>
public sealed class GatewayConnectionRegistry
{
    private readonly ConcurrentDictionary<string, byte> _connected = new();

    /// <summary>Marks a gateway connected. Returns false if it was already connected here.</summary>
    public bool TryRegister(string gatewayId) => _connected.TryAdd(gatewayId, 0);

    public void Unregister(string gatewayId) => _connected.TryRemove(gatewayId, out _);

    public bool IsConnected(string gatewayId) => _connected.ContainsKey(gatewayId);

    public int Count => _connected.Count;
}
