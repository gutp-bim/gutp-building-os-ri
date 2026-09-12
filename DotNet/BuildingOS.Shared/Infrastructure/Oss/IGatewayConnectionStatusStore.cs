using BuildingOS.Shared.Domain.Types;

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
///
/// <para>Not throwing does <b>not</b> mean collapsing failure into "disconnected" (#463). The read
/// reports three outcomes — see <see cref="GatewayConnectionState"/> — so a caller can tell a gateway
/// that is observably down from one we simply could not ask about.</para>
/// </summary>
public interface IGatewayConnectionStatusStore
{
    /// <summary>
    /// Record/refresh this replica's heartbeat for the gateway (called on connect + on each tick).
    /// <paramref name="appliedRevision"/> is the point-list ETag the gateway last reported as applied
    /// (#230 Phase 2b, ADR-0004 option A), or <c>null</c> when it has not reported one yet — the reader
    /// compares it against the twin-authoritative ETag to derive pointlist sync state.
    /// </summary>
    Task MarkConnectedAsync(
        string gatewayId, string replicaId, string? appliedRevision = null, CancellationToken ct = default);

    /// <summary>
    /// Clear the gateway's connection entry on graceful teardown — but only if <paramref name="replicaId"/>
    /// still owns it, so a stream that moved to another replica is not falsely torn down (epoch guard,
    /// mirroring <c>GatewayConnectionRegistry.Unregister</c>).
    /// </summary>
    Task MarkDisconnectedAsync(string gatewayId, string replicaId, CancellationToken ct = default);

    /// <summary>
    /// The gateway's connection state right now, with the entry attached when one is live. Never
    /// throws: a failed read is reported as <see cref="GatewayConnectionState.Unknown"/>, which is
    /// deliberately distinct from <see cref="GatewayConnectionState.Disconnected"/> (#463).
    /// </summary>
    Task<GatewayConnectionLookup> GetAsync(string gatewayId, CancellationToken ct = default);
}

/// <summary>
/// One gateway's live egress connection entry: which replica holds it, when it last beat, and the
/// point-list ETag it last reported as applied (<c>null</c> until reported — #230 Phase 2b).
/// </summary>
public sealed record GatewayConnectionStatus(
    string ReplicaId, DateTimeOffset UpdatedAt, string? AppliedRevision = null);

/// <summary>
/// The result of reading one gateway's connection state (#463). <see cref="Status"/> is only present
/// for <see cref="GatewayConnectionState.Connected"/> — both other states carry <c>null</c>, so
/// <b>the state, not the nullness of the status, is what a caller must branch on</b>. Branching on
/// "status is null" is exactly the bug this type exists to prevent.
/// </summary>
public readonly record struct GatewayConnectionLookup(
    GatewayConnectionState State, GatewayConnectionStatus? Status)
{
    /// <summary>A live heartbeat was found.</summary>
    public static GatewayConnectionLookup Live(GatewayConnectionStatus status) =>
        new(GatewayConnectionState.Connected, status);

    /// <summary>The store answered, and this gateway has no live entry (TTL-expired / never registered).</summary>
    public static GatewayConnectionLookup Disconnected { get; } =
        new(GatewayConnectionState.Disconnected, null);

    /// <summary>The store could not be read. Says nothing about whether the gateway is up.</summary>
    public static GatewayConnectionLookup Unknown { get; } =
        new(GatewayConnectionState.Unknown, null);
}
