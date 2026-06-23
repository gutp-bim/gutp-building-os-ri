using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Oss;

public class OssTelemetryQueryRouter : ITelemetryQueryRouter
{
    private static readonly TimeSpan DefaultWarmRetention = TimeSpan.FromDays(90);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHotTelemetryStore? _hot;
    private readonly IWarmTelemetryStore? _warm;
    private readonly IColdTelemetryStore? _cold;
    private readonly IAggregatedTelemetryStore? _agg;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OssTelemetryQueryRouter> _logger;
    private readonly TimeSpan _warmRetention;

    public OssTelemetryQueryRouter(
        ILogger<OssTelemetryQueryRouter> logger,
        IMemoryCache cache,
        IHotTelemetryStore? hot = null,
        IWarmTelemetryStore? warm = null,
        IColdTelemetryStore? cold = null,
        IAggregatedTelemetryStore? agg = null,
        TimeSpan? warmRetention = null)
    {
        _logger = logger;
        _cache = cache;
        _hot = hot;
        _warm = warm;
        _cold = cold;
        _agg = agg;
        _warmRetention = warmRetention ?? DefaultWarmRetention;
    }

    public async Task<ValidTelemetryData[]> QueryAsync(
        TelemetryQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Latest)
            return await QueryLatestAsync(request.PointId, cancellationToken);

        var start = (request.Start ?? DateTime.UtcNow.AddDays(-1)).ToUniversalTime();
        var end = (request.End ?? DateTime.UtcNow).ToUniversalTime();

        return request.Granularity switch
        {
            TelemetryGranularity.Hour => await QueryAggregatedAsync(request.PointId, start, end, TelemetryGranularity.Hour, cancellationToken),
            TelemetryGranularity.Day  => await QueryAggregatedAsync(request.PointId, start, end, TelemetryGranularity.Day, cancellationToken),
            _                         => await QueryRawAsync(request.PointId, start, end, cancellationToken),
        };
    }

    private async Task<ValidTelemetryData[]> QueryLatestAsync(string pointId, CancellationToken ct)
    {
        if (_hot is not null)
        {
            try
            {
                var hot = await _hot.GetAsync(pointId, ct);
                if (hot is not null)
                {
                    RecordQuery("hot", "hit");
                    return new[] { hot };
                }
            }
            catch (Exception ex)
            {
                RecordQuery("hot", "error");
                _logger.LogWarning(ex, "[Router] Hot store failed for {PointId}, falling back to warm", pointId);
            }
        }

        if (_warm is not null)
        {
            var warm = await _warm.QueryLatestAsync(pointId, ct);
            RecordQuery("warm", warm is not null ? "hit" : "miss");
            return warm is not null ? new[] { warm } : Array.Empty<ValidTelemetryData>();
        }

        RecordQuery("none", "miss");
        return Array.Empty<ValidTelemetryData>();
    }

    private static void RecordQuery(string tier, string result) =>
        BuildingOsMetrics.TelemetryQueries.Add(1,
            new KeyValuePair<string, object?>("tier", tier),
            new KeyValuePair<string, object?>("result", result));

    private async Task<ValidTelemetryData[]> QueryRawAsync(string pointId, DateTime start, DateTime end, CancellationToken ct)
    {
        var warmBoundary = DateTime.UtcNow - _warmRetention;

        // Fully within warm retention
        if (start >= warmBoundary)
            return _warm is not null
                ? await _warm.QueryAsync(pointId, start, end, ct)
                : Array.Empty<ValidTelemetryData>();

        // Fully before warm retention (cold only)
        if (end < warmBoundary)
            return _cold is not null
                ? await _cold.QueryAsync(pointId, start, end, ct)
                : Array.Empty<ValidTelemetryData>();

        // Spans the warm/cold boundary — split and merge.
        // Cold covers [start, warmBoundary), warm covers [warmBoundary, end] to avoid overlap.
        var coldEnd = warmBoundary.AddTicks(-1);
        var coldPart = _cold is not null
            ? await _cold.QueryAsync(pointId, start, coldEnd, ct)
            : Array.Empty<ValidTelemetryData>();
        var warmPart = _warm is not null
            ? await _warm.QueryAsync(pointId, warmBoundary, end, ct)
            : Array.Empty<ValidTelemetryData>();

        return coldPart.Concat(warmPart).ToArray();
    }

    private async Task<ValidTelemetryData[]> QueryAggregatedAsync(
        string pointId, DateTime start, DateTime end,
        TelemetryGranularity granularity, CancellationToken ct)
    {
        var cacheKey = $"router:{pointId}:{granularity}:{start:yyyyMMddHH}:{end:yyyyMMddHH}";

        if (_cache.TryGetValue(cacheKey, out ValidTelemetryData[]? cached) && cached is not null)
            return cached;

        ValidTelemetryData[] result;
        if (_agg is not null)
        {
            try
            {
                result = granularity == TelemetryGranularity.Hour
                    ? await _agg.QueryHourlyAsync(pointId, start, end, ct)
                    : await _agg.QueryDailyAsync(pointId, start, end, ct);
            }
            catch (Exception ex)
            {
                // Degrade to raw warm if the continuous aggregate view is unavailable.
                _logger.LogWarning(ex, "[Router] Aggregated store failed for {PointId} {Granularity}, degrading to warm raw", pointId, granularity);
                result = _warm is not null
                    ? await _warm.QueryAsync(pointId, start, end, ct)
                    : Array.Empty<ValidTelemetryData>();
            }
        }
        else
        {
            // Fallback: raw warm query when no aggregated store is configured
            result = _warm is not null
                ? await _warm.QueryAsync(pointId, start, end, ct)
                : Array.Empty<ValidTelemetryData>();
        }

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }
}
