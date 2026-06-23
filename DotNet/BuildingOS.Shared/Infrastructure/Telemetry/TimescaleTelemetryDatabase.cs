using BuildingOS.Shared.Infrastructure.Telemetry;

namespace BuildingOS.Shared.Infrastructure;

/// <summary>
/// ITelemetryDatabase backed by TimescaleDB (warm tier) + MinIO Parquet (cold tier).
/// Tier boundary: now - 3 months.
/// </summary>
public class TimescaleTelemetryDatabase : ITelemetryDatabase
{
    private static readonly TimeSpan WarmTierWindow = TimeSpan.FromDays(90);

    private readonly IWarmTelemetryStore _warm;
    private readonly IColdTelemetryStore _cold;
    private readonly Func<DateTime> _utcNow;

    public TimescaleTelemetryDatabase(
        IWarmTelemetryStore warm,
        IColdTelemetryStore cold,
        Func<DateTime>? utcNow = null)
    {
        _warm = warm;
        _cold = cold;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public Task<ValidTelemetryData[]> GetWarmTelemetries(string pointId, DateTime startTime, DateTime endTime)
        => _warm.QueryAsync(pointId, startTime, endTime);

    public async Task<ValidTelemetryData[]> GetColdTelemetries(string pointId, DateTime startTime, DateTime endTime)
    {
        var (warmStart, warmEnd, coldStart, coldEnd) = SplitRange(startTime, endTime);

        if (coldStart is null && warmStart is null)
            return Array.Empty<ValidTelemetryData>();

        if (coldStart is not null && warmStart is null)
            return await _cold.QueryAsync(pointId, coldStart.Value, coldEnd!.Value);

        if (coldStart is null && warmStart is not null)
            return await _warm.QueryAsync(pointId, warmStart.Value, warmEnd!.Value);

        // Spanning both tiers
        var coldCutoff = _utcNow().Subtract(WarmTierWindow);
        var coldTask = _cold.QueryAsync(pointId, coldStart!.Value, coldCutoff);
        var warmTask = _warm.QueryAsync(pointId, coldCutoff, warmEnd!.Value);
        await Task.WhenAll(coldTask, warmTask);

        return coldTask.Result
            .Concat(warmTask.Result)
            .OrderBy(x => x.Datetime)
            .ToArray();
    }

    public async Task<Dictionary<string, ValidTelemetryData[]>> GetColdTelemetries(string[] pointIds, DateTime startTime, DateTime endTime)
    {
        var tasks = pointIds.Select(async id => (id, data: await GetColdTelemetries(id, startTime, endTime)));
        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.id, r => r.data);
    }

    public Task<ValidTelemetryData?> GetHotTelemetry(string pointId)
        => _warm.QueryLatestAsync(pointId);

    private (DateTime? warmStart, DateTime? warmEnd, DateTime? coldStart, DateTime? coldEnd)
        SplitRange(DateTime start, DateTime end)
    {
        var cutoff = _utcNow().Subtract(WarmTierWindow);
        start = start.ToUniversalTime();
        end = end.ToUniversalTime();

        bool inCold = start < cutoff;
        bool inWarm = end > cutoff;

        DateTime? warmStart = inWarm ? (inCold ? cutoff : start) : null;
        DateTime? warmEnd = inWarm ? end : null;
        DateTime? coldStart = inCold ? start : null;
        DateTime? coldEnd = inCold ? (inWarm ? cutoff : end) : null;

        return (warmStart, warmEnd, coldStart, coldEnd);
    }
}
