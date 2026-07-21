namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Pure aggregation from raw telemetry rows into per-point rollup rows (#222).
/// Groups by PointId, computes avg/min/max over non-null values and total count (including nulls).
/// </summary>
public static class RollupAggregator
{
    /// <summary>Computes one <see cref="RollupRow"/> per distinct PointId in <paramref name="rows"/>.</summary>
    /// <param name="rows">Raw telemetry rows (all from the same building-hour, but not required).</param>
    /// <param name="hourUtc">The hour partition UTC timestamp to stamp on each rollup row. If null, derived from the rows' timestamps.</param>
    public static IReadOnlyList<RollupRow> Compute(IEnumerable<ValidTelemetryData> rows, DateTime? hourUtc = null)
    {
        var groups = new Dictionary<string, (ValidTelemetryData First, double Sum, double LocalMin, double LocalMax, int Numeric, int Total, ValidTelemetryData Last, DateTime LastTs)>(
            StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var row in rows)
        {
            var pid = row.PointId ?? string.Empty;
            if (!groups.TryGetValue(pid, out var g))
            {
                g = (row, 0, double.MaxValue, double.MinValue, 0, 0, row, DateTime.MinValue);
                order.Add(pid);
            }
            g.Total++;
            if (row.Value.HasValue)
            {
                var v = row.Value.Value;
                g.Sum += v;
                if (v < g.LocalMin) g.LocalMin = v;
                if (v > g.LocalMax) g.LocalMax = v;
                g.Numeric++;
            }
            // #152 Phase B (D3=last-in-bucket): track the latest row by timestamp for the non-numeric
            // representative value; ties broken by Id so the pick is order-independent.
            if (TelemetryTimestamp.TryParseUtc(row.Datetime, out var ts) &&
                TelemetryValueKind.IsLaterInBucket(ts, row, g.LastTs, g.Last))
            {
                g.Last = row;
                g.LastTs = ts;
            }
            groups[pid] = g;
        }

        DateTime DeriveHour(ValidTelemetryData first)
        {
            if (hourUtc.HasValue) return hourUtc.Value;
            if (TelemetryTimestamp.TryParseUtc(first.Datetime, out var utc))
                return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
            return DateTime.UtcNow.Date;
        }

        var result = new List<RollupRow>(order.Count);
        foreach (var pid in order)
        {
            var (first, sum, localMin, localMax, numeric, total, last, _) = groups[pid];
            var h = DeriveHour(first);
            var (valueType, lastText, lastBool) = TelemetryValueKind.ResolveLastInBucket(last, numeric > 0);
            result.Add(new RollupRow(
                first.PointId,
                first.Building,
                first.DeviceId,
                first.Name,
                numeric > 0 ? sum / numeric : null,
                numeric > 0 ? localMin : null,
                numeric > 0 ? localMax : null,
                total,
                h,
                valueType,
                lastText,
                lastBool));
        }
        return result;
    }
}
