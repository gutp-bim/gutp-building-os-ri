namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>One hour window to read from TimescaleDB during backfill, clipped to the requested range.</summary>
public sealed record BackfillWindow(DateTime HourUtc, DateTime ReadFromUtc, DateTime ReadToUtc);

/// <summary>
/// Pure planning for the TimescaleDB→lake backfill (#218). Splits the requested range into hour windows
/// aligned to the lake's hour partitions (clipped at both ends) and names the resulting objects with a
/// deterministic <c>part-backfill-{yyyyMMddHH}.parquet</c> key — so a re-run overwrites the same object
/// (idempotent) and the rows coexist with the live writer's parts until compaction merges them.
/// </summary>
public static class BackfillPlanner
{
    public static IReadOnlyList<BackfillWindow> HourWindows(DateTime fromUtc, DateTime toUtc)
    {
        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        var windows = new List<BackfillWindow>();
        if (to <= from)
        {
            return windows;
        }

        var hour = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc);
        while (hour < to)
        {
            var hourEnd = hour.AddHours(1);
            var readFrom = hour < from ? from : hour;
            var readTo = hourEnd < to ? hourEnd : to;
            windows.Add(new BackfillWindow(hour, readFrom, readTo));
            hour = hourEnd;
        }
        return windows;
    }

    /// <summary>Deterministic backfill object key for a building's hour partition.</summary>
    public static string BackfillKey(string building, DateTime hourUtc)
        => LakePartitionKey.HourPrefix(building, hourUtc) + $"part-backfill-{hourUtc.ToUniversalTime():yyyyMMddHH}.parquet";
}
