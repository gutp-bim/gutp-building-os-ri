using System.Text.Json;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Aggregate-on-read store for parquet mode (#215): reads raw rows from the lake over [start, end] and
/// folds them into hour/day buckets — the equivalent of the TimescaleDB continuous aggregates
/// (telemetry_hourly / telemetry_daily). The output contract matches
/// <see cref="NpgsqlAggregatedTelemetryStore"/> (one row per bucket, <c>Value</c> = average, bucket
/// start as the timestamp) so the router/web-client are unchanged; min/max/count ride along in
/// <c>Data</c> as JSON. The router caches results for 5 minutes and degrades to raw warm on error.
/// </summary>
public sealed class AggregatingParquetTelemetryStore : IAggregatedTelemetryStore
{
    private readonly IWarmTelemetryStore _raw;

    public AggregatingParquetTelemetryStore(IWarmTelemetryStore raw)
    {
        _raw = raw;
    }

    public Task<ValidTelemetryData[]> QueryHourlyAsync(
        string pointId, DateTime start, DateTime end, CancellationToken cancellationToken = default)
        => AggregateAsync(pointId, start, end, AggregationBucket.Hour, cancellationToken);

    public Task<ValidTelemetryData[]> QueryDailyAsync(
        string pointId, DateTime start, DateTime end, CancellationToken cancellationToken = default)
        => AggregateAsync(pointId, start, end, AggregationBucket.Day, cancellationToken);

    private async Task<ValidTelemetryData[]> AggregateAsync(
        string pointId, DateTime start, DateTime end, AggregationBucket bucket, CancellationToken ct)
    {
        var raw = await _raw.QueryAsync(pointId, start, end, ct).ConfigureAwait(false);
        return TelemetryAggregator.Aggregate(raw, bucket).Select(ToTelemetry).ToArray();
    }

    private static ValidTelemetryData ToTelemetry(AggregatedBucket b) => new()
    {
        Datetime = b.BucketStartUtc.ToString("O"),
        PointId  = b.PointId,
        Building = b.Building,
        DeviceId = b.DeviceId,
        Name     = b.Name,
        Value    = b.Avg, // matches the Timescale continuous-aggregate contract (value = avg)
        Data     = JsonSerializer.Serialize(new { avg = b.Avg, min = b.Min, max = b.Max, count = b.Count }),
    };
}
