using BuildingOS.Shared;

namespace BuildingOS.Shared.Domain.GatewayPointListCache;

/// <summary>
/// Persistence for the materialized gateway Point List cache (point-list-projection plan, Phase B).
/// Purely a store — freshness is decided by the caller (it compares <see cref="GatewayPointListCacheEntry.Etag"/>
/// against the existing NATS-KV revision coordinator, not anything in here).
/// </summary>
public interface IGatewayPointListCacheStore
{
    Task<GatewayPointListCacheEntry?> GetAsync(string gatewayId, CancellationToken ct = default);

    /// <summary>Full replace of the gateway's row (insert or update).</summary>
    Task UpsertAsync(
        string gatewayId, string etag, IReadOnlyList<GatewayPointEntry> entries, CancellationToken ct = default);

    /// <summary>Removes the gateway's row (e.g. deprovisioning). Returns false when there was none.</summary>
    Task<bool> DeleteAsync(string gatewayId, CancellationToken ct = default);
}
