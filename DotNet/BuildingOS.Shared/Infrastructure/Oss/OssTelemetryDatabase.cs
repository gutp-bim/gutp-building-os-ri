using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// ITelemetryDatabase backed by TimescaleDB (warm/cold) with NATS KV as hot/latest cache.
/// Hot and cold stores are optional — fall back to warm store on miss or error.
/// </summary>
public class OssTelemetryDatabase : ITelemetryDatabase
{
    private readonly IWarmTelemetryStore? _warm;
    private readonly IHotTelemetryStore? _hot;
    private readonly IColdTelemetryStore? _cold;
    private readonly ILogger<OssTelemetryDatabase> _logger;

    public OssTelemetryDatabase(
        ILogger<OssTelemetryDatabase> logger,
        IWarmTelemetryStore? warm = null,
        IHotTelemetryStore? hot = null,
        IColdTelemetryStore? cold = null)
    {
        _logger = logger;
        _warm = warm;
        _hot = hot;
        _cold = cold;
    }

    public Task<ValidTelemetryData[]> GetWarmTelemetries(string pointId, DateTime startTime, DateTime endTime)
    {
        if (_warm is not null)
            return _warm.QueryAsync(pointId, startTime, endTime);
        _logger.LogWarning("[OSS] GetWarmTelemetries({PointId}) — TIMESCALE_CONNECTION_STRING not configured", pointId);
        return Task.FromResult(Array.Empty<ValidTelemetryData>());
    }

    public Task<ValidTelemetryData[]> GetColdTelemetries(string pointId, DateTime startTime, DateTime endTime)
    {
        if (_cold is not null)
            return _cold.QueryAsync(pointId, startTime, endTime);
        // Fallback: warm store covers cold range until cold tier is populated
        if (_warm is not null)
            return _warm.QueryAsync(pointId, startTime, endTime);
        _logger.LogWarning("[OSS] GetColdTelemetries({PointId}) — no store configured", pointId);
        return Task.FromResult(Array.Empty<ValidTelemetryData>());
    }

    public async Task<Dictionary<string, ValidTelemetryData[]>> GetColdTelemetries(
        string[] pointIds, DateTime startTime, DateTime endTime)
    {
        var store = _cold as object ?? _warm;
        if (store is null)
        {
            _logger.LogWarning("[OSS] GetColdTelemetries(multi) — no store configured");
            return new Dictionary<string, ValidTelemetryData[]>();
        }

        // When the backing store can resolve many points in a single scan (the Parquet lake), use it —
        // it reads each object once instead of once per point (#215).
        if (store is IMultiPointTelemetryStore multi)
            return await multi.QueryMultiAsync(pointIds, startTime, endTime);

        var tasks = pointIds.Select(async id => (id, data: await GetColdTelemetries(id, startTime, endTime)));
        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.id, r => r.data);
    }

    public async Task<ValidTelemetryData?> GetHotTelemetry(string pointId)
    {
        if (_hot is not null)
        {
            try
            {
                var hot = await _hot.GetAsync(pointId);
                if (hot is not null) return hot;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OSS] NATS KV hot store failed for {PointId}, falling back to warm", pointId);
            }
        }

        if (_warm is not null)
            return await _warm.QueryLatestAsync(pointId);

        _logger.LogWarning("[OSS] GetHotTelemetry({PointId}) — no store configured", pointId);
        return null;
    }
}
