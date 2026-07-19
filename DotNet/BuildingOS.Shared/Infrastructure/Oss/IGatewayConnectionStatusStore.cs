namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// Cross-replica gateway egress connection state (#230 Phase 2②, ADR-0004). GatewayBridge is a
/// stateless horizontally-scaled egress control plane (ADR-0003), so the in-memory
/// <c>GatewayConnectionRegistry</c> only knows which gateways this replica holds. To surface a
/// cluster-wide "is this gateway's egress stream alive right now" to the operator UI, each replica
/// writes a per-gateway heartbeat here and lets a TTL expire it; the read side (ApiServer) aggregates it.
///
/// This is <b>best-effort observability</b>: implementations MUST NOT throw from any method (a KV
/// hiccup must never affect control routing, which flows over the per-gateway NATS subject regardless).
/// A missing entry means "not observably connected", which the UI shows alongside the last-seen signal.
/// </summary>
public interface IGatewayConnectionStatusStore
{
    /// <summary>Record/refresh this replica's heartbeat for the gateway (called on connect + on each tick).</summary>
    Task MarkConnectedAsync(string gatewayId, string replicaId, CancellationToken ct = default);

    /// <summary>
    /// Clear the gateway's connection entry on graceful teardown — but only if <paramref name="replicaId"/>
    /// still owns it, so a stream that moved to another replica is not falsely torn down (epoch guard,
    /// mirroring <c>GatewayConnectionRegistry.Unregister</c>).
    /// </summary>
    Task MarkDisconnectedAsync(string gatewayId, string replicaId, CancellationToken ct = default);

    /// <summary>The gateway's current connection entry, or <c>null</c> when none is live (TTL-expired/absent).</summary>
    Task<GatewayConnectionStatus?> GetAsync(string gatewayId, CancellationToken ct = default);
}

/// <summary>One gateway's live egress connection entry: which replica holds it and when it last beat.</summary>
public sealed record GatewayConnectionStatus(string ReplicaId, DateTimeOffset UpdatedAt);
