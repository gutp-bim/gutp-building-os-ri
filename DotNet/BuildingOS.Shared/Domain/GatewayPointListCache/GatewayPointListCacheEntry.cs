namespace BuildingOS.Shared.Domain.GatewayPointListCache;

/// <summary>
/// Materialized Point List for one gateway (point-list-projection plan, Phase B). A best-effort
/// accelerator for <c>GET /gateways/{gatewayId}/pointlist</c>'s 200 path — not a correctness-bearing
/// store: a miss or stale row costs a live Twin query (still correct, just slower), because the read
/// path always re-validates <see cref="Etag"/> against the existing NATS-KV revision coordinator
/// before trusting this row. <see cref="PayloadJson"/> is the full <c>GatewayPointEntry[]</c> array
/// (not the reduced ETag-canonical form), so a cache hit can feed the exact same DTO-mapping code a
/// live query result would.
/// </summary>
public class GatewayPointListCacheEntry
{
    public string GatewayId { get; set; } = default!;
    public string Etag { get; set; } = default!;
    public string PayloadJson { get; set; } = default!;
    /// <summary>Point count, denormalized for cheap monitoring without deserializing the payload.</summary>
    public int PointCount { get; set; }
    public DateTime MaterializedAt { get; set; }
}
