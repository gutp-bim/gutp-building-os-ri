namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>Time-bucket granularity for aggregate-on-read (#215).</summary>
public enum AggregationBucket
{
    Hour,
    Day,
}

/// <summary>One aggregated time bucket: avg/min/max over non-null values plus the row count.</summary>
public sealed record AggregatedBucket(
    DateTime BucketStartUtc, double? Avg, double? Min, double? Max, int Count,
    string? PointId, string? Building, string? DeviceId, string? Name);

/// <summary>
/// Pure aggregate-on-read folding (#215): groups raw rows into hour/day UTC buckets and computes
/// avg/min/max over the non-null values plus the total row count — the parquet-mode equivalent of the
/// TimescaleDB continuous aggregates (telemetry_hourly / telemetry_daily). Rows with an unparseable
/// timestamp are skipped; buckets are returned in ascending time order.
/// </summary>
public static class TelemetryAggregator
{
    public static IReadOnlyList<AggregatedBucket> Aggregate(IEnumerable<ValidTelemetryData> rows, AggregationBucket bucket)
    {
        var groups = new Dictionary<DateTime, List<ValidTelemetryData>>();
        var order = new List<DateTime>();

        foreach (var row in rows)
        {
            if (!TelemetryTimestamp.TryParseUtc(row.Datetime, out var utc))
            {
                continue;
            }
            var key = Truncate(utc, bucket);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<ValidTelemetryData>();
                groups[key] = list;
                order.Add(key);
            }
            list.Add(row);
        }

        order.Sort();
        var result = new List<AggregatedBucket>(order.Count);
        foreach (var key in order)
        {
            var list = groups[key];

            // Single pass: sum/min/max over the non-null values (no intermediate list allocation).
            double sum = 0, min = double.MaxValue, max = double.MinValue;
            var numeric = 0;
            foreach (var row in list)
            {
                if (!row.Value.HasValue) continue;
                var v = row.Value.Value;
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
                numeric++;
            }

            var first = list[0];
            result.Add(new AggregatedBucket(
                key,
                numeric > 0 ? sum / numeric : null,
                numeric > 0 ? min : null,
                numeric > 0 ? max : null,
                list.Count,
                first.PointId, first.Building, first.DeviceId, first.Name));
        }
        return result;
    }

    private static DateTime Truncate(DateTime utc, AggregationBucket bucket) => bucket switch
    {
        AggregationBucket.Day => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
        _ => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc),
    };
}
