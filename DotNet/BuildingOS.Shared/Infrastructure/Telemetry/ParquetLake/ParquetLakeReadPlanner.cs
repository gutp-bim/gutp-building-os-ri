namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Pure read-side planning for the Parquet lake (#214): which objects to read per hour partition
/// (compaction takes precedence over raw parts), read-time de-duplication by <c>id</c>, and the
/// descending hour window scanned by the latest-value fallback.
/// </summary>
public static class ParquetLakeReadPlanner
{
    private const string CompactPrefix = "compact-";

    /// <summary>
    /// Within each hour partition, if a compacted object (<c>compact-*.parquet</c>) exists it supersedes
    /// the raw <c>part-*.parquet</c> objects of that hour (the compactor de-duplicates them into one
    /// file). Hours without a compact object keep all their parts.
    /// </summary>
    public static IReadOnlyList<string> SelectObjectKeys(IEnumerable<string> keys)
    {
        var result = new List<string>();
        foreach (var hour in keys.GroupBy(DirectoryOf))
        {
            var compacts = hour.Where(k => FileNameOf(k).StartsWith(CompactPrefix, StringComparison.Ordinal)).ToList();
            result.AddRange(compacts.Count > 0 ? compacts : hour);
        }
        return result;
    }

    /// <summary>
    /// De-duplicates rows by <c>id</c> (last occurrence wins — overlapping part files from redelivery
    /// carry identical data), keeps rows without an id, and returns them sorted ascending by timestamp.
    /// </summary>
    public static ValidTelemetryData[] DedupById(IEnumerable<ValidTelemetryData> rows)
    {
        var byId = new Dictionary<string, ValidTelemetryData>(StringComparer.Ordinal);
        var noId = new List<ValidTelemetryData>();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Id))
            {
                noId.Add(row);
            }
            else
            {
                byId[row.Id!] = row;
            }
        }
        return byId.Values.Concat(noId)
            .OrderBy(r => r.Datetime, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The hour-partition starts to scan for a latest-value lookback, newest first: the current hour
    /// and the <paramref name="lookbackHours"/>−1 hours before it (so <c>lookbackHours</c> buckets total).
    /// </summary>
    public static IReadOnlyList<DateTime> LookbackHours(DateTime now, int lookbackHours)
    {
        var count = Math.Max(1, lookbackHours);
        var top = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var hours = new List<DateTime>(count);
        for (var i = 0; i < count; i++)
        {
            hours.Add(top.AddHours(-i));
        }
        return hours;
    }

    private static string DirectoryOf(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? string.Empty : key[..(slash + 1)];
    }

    private static string FileNameOf(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? key : key[(slash + 1)..];
    }
}
