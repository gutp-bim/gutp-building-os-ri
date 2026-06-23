using System.Globalization;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Pure planning for reading the Parquet telemetry lake (#212). Object keys follow the writer layout
/// <c>building_id={b}/year={Y}/month={MM}/day={DD}/hour={HH}/part-*.parquet</c>. Because the building
/// segment comes first, time cannot be pruned by a single prefix; the reader discovers buildings, then
/// uses month-level prefixes (<see cref="MonthPrefixes"/>) to narrow the listing, and finally
/// hour-level pruning (<see cref="IsKeyInRange"/>) to avoid reading out-of-range objects. A grace span
/// absorbs the legacy ColdExportWorker keys (whose hour reflects the window start, not the row time).
/// </summary>
public static class PartitionKeyRangePlanner
{
    /// <summary>Default ±grace applied to the query range when planning/pruning partitions.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(1);

    /// <summary>
    /// Month-level listing prefixes (<c>building_id={b}/year={Y}/month={MM}/</c>) covering
    /// [<paramref name="start"/> − grace, <paramref name="end"/> + grace] for each building. Distinct,
    /// ordered by building then month.
    /// </summary>
    public static IReadOnlyList<string> MonthPrefixes(
        IEnumerable<string> buildings, DateTime start, DateTime end, TimeSpan? grace = null)
    {
        var g = grace ?? DefaultGrace;
        var from = (start - g).ToUniversalTime();
        var to = (end + g).ToUniversalTime();
        if (to < from)
        {
            return Array.Empty<string>();
        }

        var months = new List<(int Year, int Month)>();
        var cursor = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonth = new DateTime(to.Year, to.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        while (cursor <= lastMonth)
        {
            months.Add((cursor.Year, cursor.Month));
            cursor = cursor.AddMonths(1);
        }

        var prefixes = new List<string>();
        foreach (var b in buildings.Distinct())
        {
            foreach (var (y, m) in months)
            {
                prefixes.Add($"building_id={b}/year={y:D4}/month={m:D2}/");
            }
        }
        return prefixes;
    }

    /// <summary>
    /// Whether a parquet object key's hour partition overlaps [<paramref name="start"/> − grace,
    /// <paramref name="end"/> + grace]. Keys that cannot be parsed are conservatively kept (true) so a
    /// malformed/legacy key is read rather than silently dropped.
    /// </summary>
    public static bool IsKeyInRange(string key, DateTime start, DateTime end, TimeSpan? grace = null)
    {
        var g = grace ?? DefaultGrace;
        var from = (start - g).ToUniversalTime();
        var to = (end + g).ToUniversalTime();

        if (!TryParsePartitionStart(key, out var partStart))
        {
            return true;
        }

        var partEnd = partStart.AddHours(1);
        // overlap of [partStart, partEnd) with [from, to]
        return partStart <= to && partEnd > from;
    }

    /// <summary>Extracts the distinct building ids from a set of lake object keys.</summary>
    public static IReadOnlyList<string> ExtractBuildings(IEnumerable<string> keys)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var b = ExtractSegment(key, "building_id=");
            if (b is not null)
            {
                set.Add(b);
            }
        }
        return set.ToArray();
    }

    /// <summary>Parses the hour-partition start time (UTC) embedded in a key, if present.</summary>
    public static bool TryParsePartitionStart(string key, out DateTime partitionStart)
    {
        partitionStart = default;
        var year = ExtractSegment(key, "year=");
        var month = ExtractSegment(key, "month=");
        var day = ExtractSegment(key, "day=");
        var hour = ExtractSegment(key, "hour=");
        if (year is null || month is null || day is null || hour is null)
        {
            return false;
        }

        if (int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var y) &&
            int.TryParse(month, NumberStyles.None, CultureInfo.InvariantCulture, out var m) &&
            int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var d) &&
            int.TryParse(hour, NumberStyles.None, CultureInfo.InvariantCulture, out var h) &&
            m is >= 1 and <= 12 && d is >= 1 and <= 31 && h is >= 0 and <= 23)
        {
            try
            {
                partitionStart = new DateTime(y, m, d, h, 0, 0, DateTimeKind.Utc);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Returns the value of a <c>{label}{value}/</c> segment in a key, or null when absent.</summary>
    private static string? ExtractSegment(string key, string label)
    {
        var idx = key.IndexOf(label, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }
        var startIdx = idx + label.Length;
        var slash = key.IndexOf('/', startIdx);
        return slash < 0 ? key[startIdx..] : key[startIdx..slash];
    }
}
