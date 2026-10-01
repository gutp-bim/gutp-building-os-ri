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
        // #527: only a range after the last recorded partition-key change may be pruned or learned from.
        var prunable = await _scan.CanPruneAsync(start, cancellationToken).ConfigureAwait(false);
        var known = prunable ? _scan.GetCachedBuilding(pointId) : null;
        var deduped = await ScanAsync(known is null ? null : new[] { known }).ConfigureAwait(false);
        if (prunable && known is null)
            await LearnAsync(pointId, deduped, cancellationToken).ConfigureAwait(false);
        return deduped;

        async Task<ValidTelemetryData[]> ScanAsync(IReadOnlyList<string>? buildings)
        {
            var keys = await _scan.ListKeysInRangeAsync(start, end, cancellationToken, buildings).ConfigureAwait(false);
            var selected = CapFiles(ParquetLakeReadPlanner.SelectObjectKeys(keys), pointId, start, end);
            var rows = await _scan.ReadKeysAsync(selected, pointId, start, end, cancellationToken).ConfigureAwait(false);
            return ParquetLakeReadPlanner.DedupById(rows);
        }
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
        // #527: only a range after the last recorded partition-key change may be pruned or learned from.
        var prunable = await _scan.CanPruneAsync(start, cancellationToken).ConfigureAwait(false);
        var cached = wanted.Select(p => prunable ? _scan.GetCachedBuilding(p) : null).ToList();
        IReadOnlyList<string>? filter = cached.All(b => b is not null)
            ? cached.Cast<string>().Distinct(StringComparer.Ordinal).ToList()
            : null;

        var byPoint = await ScanAsync(filter).ConfigureAwait(false);
        foreach (var id in wanted)
        {
            if (byPoint.TryGetValue(id, out var rows))
            {
                var deduped = ParquetLakeReadPlanner.DedupById(rows);
                result[id] = deduped;
                if (prunable && filter is null)
                    await LearnAsync(id, deduped, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result[id] = Array.Empty<ValidTelemetryData>();
            }
        }
        return result;

        // One pass over the objects resolves every requested point id (no per-point re-scan).
        async Task<Dictionary<string, List<ValidTelemetryData>>> ScanAsync(IReadOnlyList<string>? buildings)
        {
            var keys = await _scan.ListKeysInRangeAsync(start, end, cancellationToken, buildings).ConfigureAwait(false);
            var selected = CapFiles(ParquetLakeReadPlanner.SelectObjectKeys(keys), string.Join(",", wanted), start, end);
            return await _scan.ReadKeysMultiAsync(selected, wanted, start, end, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ValidTelemetryData?> QueryLatestAsync(string pointId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var hours = ParquetLakeReadPlanner.LookbackHours(now, _options.LatestLookbackHours).ToList();
        // Point→building pruning (#273): probe only the point's building when known — and, as for the
        // range reads, only when the whole lookback lies after the last partition-key change (#527).
        var prunable = await _scan.CanPruneAsync(hours[^1], cancellationToken).ConfigureAwait(false);
        var known = prunable ? _scan.GetCachedBuilding(pointId) : null;
        var buildings = known is not null
            ? new[] { known }
            : await _scan.GetBuildingsAsync(cancellationToken).ConfigureAwait(false);
        return (await FindNewestAsync(buildings, hours).ConfigureAwait(false)).Row;

        // The newest row of the point in the first (most recent) of `probe` hours that has one, and
        // that hour's index in `probe`. Learns from what it read.
        async Task<(ValidTelemetryData? Row, int HourIndex)> FindNewestAsync(
            IReadOnlyList<string> buildings, IReadOnlyList<DateTime> probe)
        {
            if (buildings.Count == 0) return (null, -1);
            for (var h = 0; h < probe.Count; h++)
            {
                var hour = probe[h];
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
                    if (prunable && known is null)
                        await LearnAsync(pointId, deduped, cancellationToken).ConfigureAwait(false);
                    return (deduped[^1], h); // newest in the most recent hour with data
                }
            }
            return (null, -1);
        }
    }

    /// <summary>
    /// Learns the point's building from a full scan of a range after the last recorded key change.
    /// Finding the point under more than one building there means its key changed without being
    /// recorded (#527): reads pruned before this may have missed rows. Warn, and record the change now,
    /// so no read that starts before it is pruned again.
    /// </summary>
    private async Task LearnAsync(string pointId, IReadOnlyList<ValidTelemetryData> rows, CancellationToken ct)
    {
        if (_scan.LearnBuilding(pointId, rows) is not { } buildings) return;
        BuildingOsMetrics.LakePointBuildingConflicts.Add(1);
        _logger.LogWarning(
            "Lake point {PointId} has telemetry under more than one building partition ({Buildings}): its " +
            "partition key changed without being recorded (twin topology change, or the #527 key migration). " +
            "Recording the change now; reads pruned to one building before this may have missed rows. " +
            "After a twin change, record it explicitly: POST /api/v1/system/lake/point-buildings/reset",
            ForLog(pointId), ForLog(string.Join(", ", buildings)));
        try
        {
            await _scan.MarkKeysChangedAsync(DateTime.UtcNow, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not record the lake partition-key change for point {PointId}", ForLog(pointId));
        }
    }

    // The point id comes from the request; strip control characters so it cannot forge log lines.
    private static readonly System.Text.RegularExpressions.Regex ControlChars =
        new(@"\p{C}", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static string ForLog(string value) => ControlChars.Replace(value, "_");

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

        var ordered = keys
            .OrderByDescending(k => PartitionKeyRangePlanner.TryParsePartitionStart(k, out var t) ? t : DateTime.MinValue)
            .ToList();

        // Tell the caller (#499): data is only complete from the end of the newest dropped partition.
        // The grace mirrors the read planner's — a legacy key's hour is its window start, so its rows
        // can run past the partition hour. An unparseable dropped key sorts last (MinValue) and so only
        // decides the bound when every dropped key is unparseable; then nothing is known to be covered
        // and the requested start is the honest bound.
        var newestDropped = ordered[_options.QueryMaxFiles];
        var coveredFrom = PartitionKeyRangePlanner.TryParsePartitionStart(newestDropped, out var droppedStart)
            ? droppedStart.AddHours(1) + PartitionKeyRangePlanner.DefaultGrace
            : start;
        // Never past the requested end: a cut inside the newest hour means nothing is known complete,
        // and a bound beyond the range would send a client re-fetching the gap outside it.
        TelemetryQueryCompleteness.ReportTruncated(coveredFrom > end ? end : coveredFrom);

        return ordered.Take(_options.QueryMaxFiles).ToList();
    }
}
