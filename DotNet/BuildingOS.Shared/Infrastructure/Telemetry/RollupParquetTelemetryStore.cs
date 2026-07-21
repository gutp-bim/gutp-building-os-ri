using System.Text.Json;
using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Aggregated telemetry store backed by pre-computed hourly rollup Parquet objects (#222).
/// Reads <c>agg_hourly/...</c> objects written by <see cref="CompactionWorker"/>. For any hour that
/// has no rollup (e.g. not yet compacted), falls back to the <see cref="AggregatingParquetTelemetryStore"/>
/// (aggregate-on-read). This replaces <see cref="AggregatingParquetTelemetryStore"/> as the primary
/// <see cref="IAggregatedTelemetryStore"/> in parquet mode while preserving the same degradation
/// contract (2xx on missing data, router-level cache absorbs repeated queries).
/// </summary>
public sealed class RollupParquetTelemetryStore : IAggregatedTelemetryStore
{
    private readonly IBlobStorage _storage;
    private readonly IMemoryCache _cache;
    private readonly AggregatingParquetTelemetryStore _fallback;
    private readonly ILogger<RollupParquetTelemetryStore> _logger;

    private const string Bucket = "cold";
    private const string BuildingsCacheKey = "rollup:buildings";
    private static readonly TimeSpan BuildingsCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Max concurrent rollup-object GETs per aggregate query (bounds S3 fan-out).</summary>
    private const int MaxProbeConcurrency = 16;

    public RollupParquetTelemetryStore(
        IBlobStorage storage,
        IMemoryCache cache,
        AggregatingParquetTelemetryStore fallback,
        ILogger<RollupParquetTelemetryStore> logger)
    {
        _storage = storage;
        _cache = cache;
        _fallback = fallback;
        _logger = logger;
    }

    public Task<ValidTelemetryData[]> QueryHourlyAsync(
        string pointId, DateTime start, DateTime end, CancellationToken ct = default)
        => QueryAsync(pointId, start, end, AggregationBucket.Hour, ct);

    public Task<ValidTelemetryData[]> QueryDailyAsync(
        string pointId, DateTime start, DateTime end, CancellationToken ct = default)
        => QueryAsync(pointId, start, end, AggregationBucket.Day, ct);

    private async Task<ValidTelemetryData[]> QueryAsync(
        string pointId, DateTime start, DateTime end, AggregationBucket bucket, CancellationToken ct)
    {
        var buildings = await GetBuildingsAsync(ct).ConfigureAwait(false);

        var startHour = TruncateHour(start);
        var endHour   = TruncateHour(end);
        if (end > endHour) endHour = endHour.AddHours(1); // inclusive

        var hours = new List<DateTime>();
        for (var h = startHour; h < endHour; h = h.AddHours(1)) hours.Add(h);

        // A full-hour rollup can only serve an hour wholly inside [start, end]; boundary partial hours
        // (and any hour whose rollup is missing) are served by a coalesced aggregate-on-read instead.
        bool IsFull(DateTime h) => h >= start && h.AddHours(1) <= end;

        // Phase 1 — probe the rollup object for every FULL hour CONCURRENTLY (bounded). The old code
        // did one sequential S3 GET per (hour × building); for a 30-day window that is hundreds of
        // serial round-trips. We also prune the building scan: a point lives in exactly one building,
        // so once any hour resolves it, the remaining hours probe only that building.
        string? resolvedBuilding = null;
        var rollupByHour = new System.Collections.Concurrent.ConcurrentDictionary<DateTime, ValidTelemetryData>();
        using (var gate = new SemaphoreSlim(MaxProbeConcurrency))
        {
            var probes = hours.Where(IsFull).Select(async hour =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var hit = await ProbeRollupAsync(pointId, hour, resolvedBuilding, buildings, ct)
                        .ConfigureAwait(false);
                    if (hit is not null)
                    {
                        resolvedBuilding ??= hit.Building; // benign race: every hit shares one building
                        rollupByHour[hour] = ToTelemetry(hit);
                    }
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(probes).ConfigureAwait(false);
        }

        // Phase 2 — walk hours in order. Emit rollup hits; coalesce consecutive hours WITHOUT a rollup
        // (partial boundaries or genuinely missing) into a SINGLE aggregate-on-read range. A gap of N
        // hours then costs one lake scan instead of N — the dominant win when rollups are absent
        // (e.g. a freshly-ingested range not yet compacted), which previously made 7d/30d aggregate
        // queries scan the lake once per hour.
        var result = new List<ValidTelemetryData>();
        DateTime? gapStart = null, gapEnd = null;

        async Task FlushGapAsync()
        {
            if (gapStart is null) return;
            var fbStart = gapStart.Value < start ? start : gapStart.Value;
            var fbEnd   = gapEnd!.Value > end ? end : gapEnd.Value;
            if (fbEnd > fbStart)
            {
                var fb = await _fallback.QueryHourlyAsync(pointId, fbStart, fbEnd, ct).ConfigureAwait(false);
                result.AddRange(fb);
            }
            gapStart = gapEnd = null;
        }

        foreach (var hour in hours)
        {
            if (rollupByHour.TryGetValue(hour, out var rolled))
            {
                await FlushGapAsync().ConfigureAwait(false);
                result.Add(rolled);
            }
            else
            {
                gapStart ??= hour;
                gapEnd = hour.AddHours(1);
            }
        }
        await FlushGapAsync().ConfigureAwait(false);

        // Coalesced gap flushes append their own (ascending) buckets between rollup hits, so sort by
        // timestamp for a stable ascending contract regardless of interleaving.
        result.Sort((a, b) => string.CompareOrdinal(a.Datetime, b.Datetime));

        // Re-aggregate to daily buckets if requested (rollups are always per-hour).
        return bucket == AggregationBucket.Day
            ? ReAggregateToDayBuckets(result, start, end)
            : result.ToArray();
    }

    /// <summary>
    /// Reads the rollup object for one hour and returns the row for <paramref name="pointId"/>, or null
    /// if absent. When <paramref name="preferredBuilding"/> is known, only that building is probed (a
    /// point lives in exactly one building); otherwise every building is scanned, first hit wins.
    /// </summary>
    private async Task<RollupRow?> ProbeRollupAsync(
        string pointId, DateTime hour, string? preferredBuilding, IReadOnlyList<string> buildings, CancellationToken ct)
    {
        var candidates = preferredBuilding is not null ? new[] { preferredBuilding } : buildings;
        foreach (var building in candidates)
        {
            var stream = await _storage.GetAsync(Bucket, RollupPartitionKey.AggKey(building, hour), ct)
                .ConfigureAwait(false);
            if (stream is null) continue;
            try
            {
                var rows = await RollupSerializer.ReadAsync(stream, ct).ConfigureAwait(false);
                var row = rows.FirstOrDefault(r => r.PointId == pointId);
                if (row is not null) return row;
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        return null;
    }

    private async Task<IReadOnlyList<string>> GetBuildingsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(BuildingsCacheKey, out IReadOnlyList<string>? cached) && cached is not null)
            return cached;

        // Discover buildings from main lake keys first (production path); fall back to rollup-only
        // prefix so tests that seed only rollup objects still resolve buildings.
        var lakeKeys   = await _storage.ListAsync(Bucket, "building_id=", ct).ConfigureAwait(false);
        var rollupKeys = await _storage.ListAsync(Bucket, "agg_hourly/building_id=", ct).ConfigureAwait(false);

        var fromLake   = PartitionKeyRangePlanner.ExtractBuildings(lakeKeys);
        // For rollup keys (agg_hourly/building_id={b}/...) strip the leading segment before extracting.
        var rollupTrimmed = rollupKeys.Select(k =>
            k.StartsWith("agg_hourly/", StringComparison.Ordinal) ? k["agg_hourly/".Length..] : k).ToList();
        var fromRollup = PartitionKeyRangePlanner.ExtractBuildings(rollupTrimmed);

        var buildings = fromLake.Concat(fromRollup).Distinct(StringComparer.Ordinal).ToList();
        _cache.Set(BuildingsCacheKey, (IReadOnlyList<string>)buildings, BuildingsCacheTtl);
        return buildings;
    }

    private static ValidTelemetryData[] ReAggregateToDayBuckets(List<ValidTelemetryData> hourly, DateTime start, DateTime end)
    {
        // Each hourly row carries avg/min/max/count in Data JSON (from ToTelemetry). We weight by count
        // to compute a correct daily weighted average instead of a simple average of hourly averages.
        var groups = new Dictionary<DateTime, (double WSum, double WCount, double LocalMin, double LocalMax, int Total, ValidTelemetryData First, ValidTelemetryData Last, DateTime LastTs)>();
        var order = new List<DateTime>();

        foreach (var row in hourly)
        {
            if (!TelemetryTimestamp.TryParseUtc(row.Datetime, out var utc)) continue;
            var day = new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
            if (!groups.TryGetValue(day, out var g))
            {
                g = (0, 0, double.MaxValue, double.MinValue, 0, row, row, DateTime.MinValue);
                order.Add(day);
            }
            var hourCount = ExtractCount(row.Data);
            g.Total += hourCount;
            if (row.Value.HasValue && hourCount > 0)
            {
                var avg = row.Value.Value;
                g.WSum   += avg * hourCount;
                g.WCount += hourCount;
            }
            // Use per-hour true min/max from Data JSON rather than the hourly avg, which would
            // produce incorrect daily min/max (avg of avgs ≠ true min/max of raw rows).
            var (hourMin, hourMax) = ExtractMinMax(row.Data);
            var effectiveMin = hourMin ?? row.Value;
            var effectiveMax = hourMax ?? row.Value;
            if (effectiveMin.HasValue && effectiveMin.Value < g.LocalMin) g.LocalMin = effectiveMin.Value;
            if (effectiveMax.HasValue && effectiveMax.Value > g.LocalMax) g.LocalMax = effectiveMax.Value;
            // #152 Phase B: last-in-day representative = the latest hour's last-in-bucket value.
            if (utc >= g.LastTs)
            {
                g.Last = row;
                g.LastTs = utc;
            }
            groups[day] = g;
        }

        order.Sort();
        return order.Select(day =>
        {
            var (wsum, wcount, localMin, localMax, total, first, last, _) = groups[day];
            var avg = wcount > 0 ? wsum / wcount : (double?)null;
            var (valueType, lastText, lastBool) = TelemetryValueKind.ResolveLastInBucket(last, wcount > 0);
            return new ValidTelemetryData
            {
                Datetime = day.ToString("O"),
                PointId  = first.PointId,
                Building = first.Building,
                DeviceId = first.DeviceId,
                Name     = first.Name,
                Value    = avg,
                Data     = JsonSerializer.Serialize(new
                {
                    avg,
                    min   = wcount > 0 ? localMin : (double?)null,
                    max   = wcount > 0 ? localMax : (double?)null,
                    count = total,
                }),
                ValueType = valueType,
                ValueText = lastText,
                ValueBool = lastBool,
            };
        }).ToArray();
    }

    private static (double? Min, double? Max) ExtractMinMax(string? data)
    {
        if (data is null) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            double? min = root.TryGetProperty("min", out var minEl) && minEl.ValueKind == JsonValueKind.Number
                ? minEl.GetDouble() : null;
            double? max = root.TryGetProperty("max", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number
                ? maxEl.GetDouble() : null;
            return (min, max);
        }
        catch { return (null, null); }
    }

    private static int ExtractCount(string? data)
    {
        if (data is null) return 1; // no metadata → treat as single point
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("count", out var c) && c.TryGetInt32(out var n))
                return n > 0 ? n : 1;
        }
        catch { /* ignore malformed JSON */ }
        return 1;
    }

    private static ValidTelemetryData ToTelemetry(RollupRow r) => new()
    {
        Datetime = r.HourUtc.ToString("O"),
        PointId  = r.PointId,
        Building = r.Building,
        DeviceId = r.DeviceId,
        Name     = r.Name,
        Value    = r.Avg,
        Data     = JsonSerializer.Serialize(new { avg = r.Avg, min = r.MinValue, max = r.MaxValue, count = r.Count }),
        // #152 Phase B: surface the rollup's non-numeric last-in-bucket value (null → numeric).
        ValueType = r.ValueType,
        ValueText = r.ValueText,
        ValueBool = r.ValueBool,
    };

    private static DateTime TruncateHour(DateTime dt)
        => new(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, DateTimeKind.Utc);
}
