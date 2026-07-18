using System.Collections.Concurrent;

namespace BuildingOS.GatewayBridge.Infrastructure;

/// <summary>
/// A single gateway egress stream held by this bridge replica. Its <see cref="SupersededToken"/> is
/// cancelled when a newer connection for the same gateway takes over the slot, signalling the older
/// stream to tear itself down (supersede / last-writer-wins).
/// </summary>
public sealed class GatewayConnection : IDisposable
{
    private readonly CancellationTokenSource _superseded = new();

    internal GatewayConnection(string gatewayId) => GatewayId = gatewayId;

    public string GatewayId { get; }

    /// <summary>Cancelled when a newer connection for the same gateway supersedes this one.</summary>
    public CancellationToken SupersededToken => _superseded.Token;

    public bool IsSuperseded => _superseded.IsCancellationRequested;

    internal void MarkSuperseded()
    {
        try { _superseded.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down — nothing to signal */ }
    }

    public void Dispose() => _superseded.Dispose();
}

/// <summary>
/// In-memory registry of gateways currently connected to THIS bridge replica. There is no persistent
/// state (plan §3-3): on pod restart the registry is rebuilt as gateways reconnect.
/// <para>
/// Multi-connection policy is <b>supersede</b>: a gateway may reconnect while a stale stream is still
/// half-open, so a new <see cref="Register"/> is always accepted and the previous connection (if any)
/// is signalled to close via its <see cref="GatewayConnection.SupersededToken"/>. This keeps exactly
/// one active egress stream per gateway on a replica without the old AlreadyExists lock-out, where a
/// lingering stream blocked every reconnect until the pod restarted.
/// </para>
/// </summary>
public sealed class GatewayConnectionRegistry
{
    private readonly ConcurrentDictionary<string, GatewayConnection> _connected = new();

    /// <summary>
    /// Registers a new connection for the gateway and supersedes any existing one on this replica
    /// (cancelling its <see cref="GatewayConnection.SupersededToken"/>). Always succeeds.
    /// </summary>
    public GatewayConnection Register(string gatewayId)
    {
        var connection = new GatewayConnection(gatewayId);
        _connected.AddOrUpdate(
            gatewayId,
            connection,
            (_, previous) =>
            {
                previous.MarkSuperseded();
                return connection;
            });
        return connection;
    }

    /// <summary>
    /// Removes the connection <b>only if it still owns the gateway's slot</b> (epoch guard): a
    /// superseded connection tearing down late must not evict the newer one that replaced it.
    /// </summary>
    public void Unregister(GatewayConnection connection)
        => ((ICollection<KeyValuePair<string, GatewayConnection>>)_connected)
            .Remove(new KeyValuePair<string, GatewayConnection>(connection.GatewayId, connection));

    public bool IsConnected(string gatewayId) => _connected.ContainsKey(gatewayId);

    public int Count => _connected.Count;
}
