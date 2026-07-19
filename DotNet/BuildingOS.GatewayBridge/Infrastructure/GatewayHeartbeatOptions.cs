namespace BuildingOS.GatewayBridge.Infrastructure;

/// <summary>
/// Heartbeat cadence + this replica's identity for the gateway connection-state store (#230, ADR-0004).
/// <see cref="Interval"/> is how often a live egress stream refreshes its KV entry; the store's TTL
/// (default 3× the interval) is the crash backstop. <see cref="ReplicaId"/> distinguishes replicas so
/// teardown only clears an entry this replica still owns (epoch guard).
/// </summary>
public sealed record GatewayHeartbeatOptions(TimeSpan Interval, string ReplicaId);
