using BuildingOS.Shared;
using Microsoft.Extensions.Caching.Memory;

namespace BuildingOs.ApiServer.GatewayProvisioning;

/// <summary>
/// Short-lived store of a gateway's point-list snapshots keyed by ETag, so `?since={etag}` can be
/// diffed against the client's last-known revision (#224/diff). Best-effort: a miss (eviction /
/// replica restart) falls back to a full response — no durability guarantee.
/// </summary>
public interface IGatewayPointListSnapshotStore
{
    /// <summary>The snapshot the gateway saw at <paramref name="etag"/>, or null when not retained.</summary>
    GatewayPointEntry[]? Get(string gatewayId, string etag);

    /// <summary>Retain the current snapshot under its ETag for later <c>?since=</c> diffs.</summary>
    void Save(string gatewayId, string etag, GatewayPointEntry[] entries);
}

/// <summary>IMemoryCache-backed snapshot store with a TTL (bounded, per-replica, best-effort).</summary>
public sealed class MemoryGatewayPointListSnapshotStore(IMemoryCache cache) : IGatewayPointListSnapshotStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private static string Key(string gatewayId, string etag) => $"gwpl-snapshot:{gatewayId}:{etag}";

    public GatewayPointEntry[]? Get(string gatewayId, string etag)
        => cache.TryGetValue(Key(gatewayId, etag), out GatewayPointEntry[]? entries) ? entries : null;

    public void Save(string gatewayId, string etag, GatewayPointEntry[] entries)
        => cache.Set(Key(gatewayId, etag), entries, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Ttl, // TTL bounds memory (matches OxiGraph cache usage)
        });
}
