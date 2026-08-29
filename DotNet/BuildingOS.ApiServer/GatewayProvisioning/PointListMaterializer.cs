using BuildingOS.Shared;
using BuildingOS.Shared.Domain.GatewayPointListCache;
using BuildingOS.Shared.Infrastructure;

namespace BuildingOs.ApiServer.GatewayProvisioning;

/// <summary>
/// Rebuilds the materialized Point List cache (point-list-projection plan, Phase B) from the twin —
/// always via the same <see cref="IDigitalTwinDatabase.ListGatewayPointList"/> that backs the live
/// read path, so the cache can never diverge from what a live query would return at rebuild time.
/// </summary>
public interface IPointListMaterializer
{
    /// <summary>Rebuilds one gateway's row. Cheap (#259/#260: ~91ms at 1k points) — safe to await
    /// inline on a request path, e.g. after a single point's metadata changes.</summary>
    Task RebuildGatewayAsync(string gatewayId, CancellationToken ct = default);

    /// <summary>Rebuilds every gateway the twin currently knows about. Bounded concurrency so a large
    /// twin does not fire hundreds of simultaneous SPARQL queries at the single embedded OxiGraph
    /// process. Intended to run off the request path (see <see cref="PointListMaterializerSweepService"/>).</summary>
    Task RebuildAllAsync(CancellationToken ct = default);
}

public sealed class PointListMaterializer(
    IDigitalTwinDatabase digitalTwinDatabase,
    IGatewayPointListCacheStore cacheStore,
    ILogger<PointListMaterializer> logger) : IPointListMaterializer
{
    private const int MaxConcurrentRebuilds = 8;

    public async Task RebuildGatewayAsync(string gatewayId, CancellationToken ct = default)
    {
        var entries = await digitalTwinDatabase.ListGatewayPointList(gatewayId).ConfigureAwait(false);
        await cacheStore
            .UpsertAsync(gatewayId, PointListEtag.Compute(entries), entries, ct)
            .ConfigureAwait(false);
    }

    public async Task RebuildAllAsync(CancellationToken ct = default)
    {
        var gatewayIds = await digitalTwinDatabase.ListGatewayIds().ConfigureAwait(false);
        using var throttle = new SemaphoreSlim(MaxConcurrentRebuilds);

        var tasks = gatewayIds.Select(async gatewayId =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RebuildGatewayAsync(gatewayId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One malformed/unreachable gateway must not blank out every other gateway's cache row
                // (same per-iteration try/catch idiom as OxiGraphSeedHostedService's point-list publish
                // loop). A failed rebuild just means the next read for this gateway falls back to a
                // live Twin query — the cache is an accelerator, not a correctness gate.
                logger.LogWarning(ex,
                    "Point-list materialization failed for gateway {GatewayId}; reads for it fall back to a live Twin query",
                    gatewayId);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
