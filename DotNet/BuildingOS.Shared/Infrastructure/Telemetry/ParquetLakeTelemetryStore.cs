using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>Tuning for the parquet-mode warm+cold store (#214).</summary>
public sealed record ParquetLakeTelemetryStoreOptions
{
    /// <summary>How many hours the latest-value fallback scans back from now (newest first).</summary>
    public int LatestLookbackHours { get; init; } = 24;

    /// <summary>Max objects a single range query may read; 0 = unlimited. Over the cap → partial result + warning.</summary>
    public int QueryMaxFiles { get; init; }
}

/// <summary>
/// Reads the unified Parquet lake for BOTH the warm and cold tiers (#214). The same instance is
/// injected as <see cref="IWarmTelemetryStore"/> and <see cref="IColdTelemetryStore"/>, so the existing
/// <c>OssTelemetryQueryRouter</c> is unchanged — whichever side of the warm/cold boundary a query lands
/// on, it reads the same lake. Compaction objects take precedence over raw parts, overlapping rows are
/// de-duplicated by id, and the latest-value fallback scans recent hour partitions newest-first (the
/// hot KV remains the primary latest source in the router).
/// </summary>
public sealed class ParquetLakeTelemetryStore : IWarmTelemetryStore, IColdTelemetryStore, IMultiPointTelemetryStore
{
    private readonly ParquetLakeScan _scan;
    private readonly ParquetLakeTelemetryStoreOptions _options;
    private readonly ILogger<ParquetLakeTelemetryStore> _logger;

    public ParquetLakeTelemetryStore(
        IBlobStorage storage,
        IMemoryCache cache,
        ParquetLakeTelemetryStoreOptions options,
        ILogger<ParquetLakeTelemetryStore> logger)
    {
        _scan = new ParquetLakeScan(storage, cache);
        _options = options;
        _logger = logger;
    }

    public async Task<ValidTelemetryData[]> QueryAsync(
        string pointId, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        // Point→building pruning (#273): if we already know this point's building, scan only it
        // instead of every building in the lake; otherwise scan all and learn the building below.
        var known = _scan.GetCachedBuilding(pointId);
        var keys = await _scan.ListKeysInRangeAsync(
            start, end, cancellationToken, known is null ? null : new[] { known }).ConfigureAwait(false);
        var selected = ParquetLakeReadPlanner.SelectObjectKeys(keys);
        selected = CapFiles(selected, pointId, start, end);

        var rows = await _scan.ReadKeysAsync(selected, pointId, start, end, cancellationToken).ConfigureAwait(false);
        var deduped = ParquetLakeReadPlanner.DedupById(rows);
        if (known is null && deduped.Length > 0)
            _scan.CacheBuilding(pointId, deduped[0].Building);
        return deduped;
    }

    public async Task<Dictionary<string, ValidTelemetryData[]>> QueryMultiAsync(
        string[] pointIds, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var wanted = new HashSet<string>(pointIds, StringComparer.Ordinal);
        var result = new Dictionary<string, ValidTelemetryData[]>(pointIds.Length);
        if (wanted.Count == 0)
        {
            return result;
        }

        // Point→building pruning (#273): prune only when EVERY requested point's building is known,
        // to the (distinct) union of those buildings; otherwise scan all (a single unknown point in a
        // different building would otherwise be missed).
        var cached = wanted.Select(p => _scan.GetCachedBuilding(p)).ToList();
        IReadOnlyList<string>? filter = cached.All(b => b is not null)
            ? cached.Cast<string>().Distinct(StringComparer.Ordinal).ToList()
            : null;

        var keys = await _scan.ListKeysInRangeAsync(start, end, cancellationToken, filter).ConfigureAwait(false);
        var selected = ParquetLakeReadPlanner.SelectObjectKeys(keys);
        selected = CapFiles(selected, string.Join(",", wanted), start, end);

        // One pass over the objects resolves every requested point id (no per-point re-scan).
        var byPoint = await _scan.ReadKeysMultiAsync(selected, wanted, start, end, cancellationToken).ConfigureAwait(false);
        foreach (var id in wanted)
        {
            if (byPoint.TryGetValue(id, out var rows))
            {
                var deduped = ParquetLakeReadPlanner.DedupById(rows);
                result[id] = deduped;
                if (filter is null && deduped.Length > 0)
                    _scan.CacheBuilding(id, deduped[0].Building);
            }
            else
            {
                result[id] = Array.Empty<ValidTelemetryData>();
            }
        }
        return result;
    }

    public async Task<ValidTelemetryData?> QueryLatestAsync(string pointId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        // Point→building pruning (#273): probe only the point's building when known.
        var known = _scan.GetCachedBuilding(pointId);
        var buildings = known is not null
            ? new[] { known }
            : await _scan.GetBuildingsAsync(cancellationToken).ConfigureAwait(false);
        if (buildings.Count == 0)
        {
            return null;
        }

        foreach (var hour in ParquetLakeReadPlanner.LookbackHours(now, _options.LatestLookbackHours))
        {
            // List each building's hour partition concurrently so fallback latency does not grow
            // linearly with the building count (the listings are independent reads).
            var perBuilding = await Task.WhenAll(
                buildings.Select(b => _scan.ListHourKeysAsync(b, hour, cancellationToken))).ConfigureAwait(false);
            var keys = perBuilding.SelectMany(x => x).ToList();
            if (keys.Count == 0) continue;

            var selected = ParquetLakeReadPlanner.SelectObjectKeys(keys);
            var rows = await _scan.ReadKeysAsync(selected, pointId, hour, now, cancellationToken).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                var deduped = ParquetLakeReadPlanner.DedupById(rows); // ascending by time
                if (known is null) _scan.CacheBuilding(pointId, deduped[^1].Building);
                return deduped[^1]; // newest in the most recent hour with data
            }
        }
        return null;
    }

    private IReadOnlyList<string> CapFiles(IReadOnlyList<string> keys, string queryLabel, DateTime start, DateTime end)
    {
        if (_options.QueryMaxFiles <= 0 || keys.Count <= _options.QueryMaxFiles)
        {
            return keys;
        }

        // Partial result: read the most recent partitions and warn, rather than throwing — the
        // router/controller surfaces data with a logged gap. Order by the partition's hour timestamp
        // (NOT the raw object key, which starts with building_id= and would sort by building, dropping
        // newer hours of an early-sorting building), so "most recent" holds across buildings.
        _logger.LogWarning(
            "ParquetLakeTelemetryStore: query for {Query} [{Start:o},{End:o}] matched {Matched} objects, " +
            "over PARQUET_QUERY_MAX_FILES={Max}; returning a partial result from the most recent {Max} objects",
            queryLabel, start, end, keys.Count, _options.QueryMaxFiles);

        return keys
            .OrderByDescending(k => PartitionKeyRangePlanner.TryParsePartitionStart(k, out var t) ? t : DateTime.MinValue)
            .Take(_options.QueryMaxFiles)
            .ToList();
    }
}
