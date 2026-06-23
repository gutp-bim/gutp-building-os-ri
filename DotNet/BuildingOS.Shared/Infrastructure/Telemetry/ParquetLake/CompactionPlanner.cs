namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>One hour partition to compact: its source objects merge (id-deduped) into <see cref="CompactKey"/>.</summary>
public sealed record CompactionTarget(
    string Building, DateTime HourUtc, IReadOnlyList<string> SourceKeys, string CompactKey);

/// <summary>
/// Pure target selection for the lake compactor (#217). The 5–15 min flushes leave many small
/// <c>part-*.parquet</c> objects per building-hour; once an hour partition is <i>settled</i> (its hour
/// has ended plus a grace margin, so no more parts will land) and holds enough parts, they are merged —
/// together with any pre-existing <c>compact-*.parquet</c> — into one deterministic
/// <c>compact-{yyyyMMddHH}.parquet</c>. Re-including the existing compact makes a re-run after new parts
/// arrive (or after an interrupted run) converge without loss or duplication.
/// </summary>
public static class CompactionPlanner
{
    public static readonly TimeSpan DefaultSettleGrace = TimeSpan.FromMinutes(30);
    private const string CompactPrefix = "compact-";

    /// <summary>An hour is settled when now is past its end (<paramref name="hourUtc"/>+1h) plus the grace.</summary>
    public static bool IsHourSettled(DateTime hourUtc, DateTime nowUtc, TimeSpan settleGrace)
        => nowUtc >= hourUtc.AddHours(1) + settleGrace;

    public static IReadOnlyList<CompactionTarget> Plan(
        IEnumerable<string> keys, DateTime nowUtc, TimeSpan settleGrace, int minParts = 2)
    {
        var groups = new Dictionary<(string Building, DateTime Hour), (List<string> Parts, string? Compact)>();
        foreach (var key in keys)
        {
            if (!key.EndsWith(".parquet", StringComparison.Ordinal)) continue;
            var building = ExtractBuilding(key);
            if (building is null) continue;
            if (!PartitionKeyRangePlanner.TryParsePartitionStart(key, out var hour)) continue;

            var groupKey = (building, hour);
            if (!groups.TryGetValue(groupKey, out var g))
            {
                g = (new List<string>(), null);
            }
            if (FileNameOf(key).StartsWith(CompactPrefix, StringComparison.Ordinal))
            {
                g.Compact = key;
            }
            else
            {
                g.Parts.Add(key);
            }
            groups[groupKey] = g;
        }

        var targets = new List<CompactionTarget>();
        foreach (var ((building, hour), (parts, compact)) in groups)
        {
            if (!IsHourSettled(hour, nowUtc, settleGrace)) continue;

            // Act when enough fresh parts have accumulated, OR a compact already exists and any leftover
            // part remains (a re-run after new parts arrived, or after an interrupted delete — fold it in
            // so a settled hour converges to a single object). An hour with only a compact is done.
            var act = parts.Count >= minParts || (compact is not null && parts.Count >= 1);
            if (!act) continue;

            parts.Sort(StringComparer.Ordinal);
            var sources = new List<string>(parts.Count + 1);
            if (compact is not null) sources.Add(compact); // existing compact first; parts last-win on dedup
            sources.AddRange(parts);

            targets.Add(new CompactionTarget(
                building, hour, sources,
                LakePartitionKey.HourPrefix(building, hour) + $"compact-{hour:yyyyMMddHH}.parquet"));
        }
        return targets;
    }

    private static string? ExtractBuilding(string key)
    {
        foreach (var segment in key.Split('/'))
        {
            if (segment.StartsWith("building_id=", StringComparison.Ordinal))
            {
                return segment["building_id=".Length..];
            }
        }
        return null;
    }

    private static string FileNameOf(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? key : key[(slash + 1)..];
    }
}
