using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Decorator over <see cref="IWarmTelemetryStore"/> that appends unflushed JetStream messages for
/// queries whose end time is within the tail-merge lookback window (#220). The lake scan and the
/// JetStream fetch run concurrently; on any tail fetch error the store degrades gracefully to
/// lake-only results so availability is never compromised.
/// </summary>
public sealed class TailMergedTelemetryStore : IWarmTelemetryStore, IMultiPointTelemetryStore
{
    private readonly IWarmTelemetryStore _inner;
    private readonly IJetStreamTailReader _tailReader;
    private readonly TailMergeOptions _options;
    private readonly ILogger<TailMergedTelemetryStore> _logger;

    public TailMergedTelemetryStore(
        IWarmTelemetryStore inner,
        IJetStreamTailReader tailReader,
        TailMergeOptions options,
        ILogger<TailMergedTelemetryStore>? logger = null)
    {
        _inner      = inner;
        _tailReader = tailReader;
        _options    = options;
        _logger     = logger ?? NullLogger<TailMergedTelemetryStore>.Instance;
    }

    public async Task<ValidTelemetryData[]> QueryAsync(
        string pointId, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (!TailMergePolicy.ShouldMergeTail(end, now, _options.LookbackSec))
        {
            return await _inner.QueryAsync(pointId, start, end, ct).ConfigureAwait(false);
        }

        // Parallel fetch — lake scan and tail read at the same time.
        var lakeTask = _inner.QueryAsync(pointId, start, end, ct);
        var tailTask = TryReadTailAsync(new HashSet<string>(StringComparer.Ordinal) { pointId }, start, end, ct);
        await Task.WhenAll(lakeTask, tailTask).ConfigureAwait(false);

        var lakeRows = lakeTask.Result;
        var tailRows = tailTask.Result;

        return MergeTail(lakeRows, tailRows);
    }

    /// <summary>
    /// #510: the lake half is one multi-point scan when the inner store supports it (the Parquet lake
    /// reads each object once for every point); the tail, when the range reaches it, is still read
    /// and merged per point, exactly as <see cref="QueryAsync"/> does for one.
    /// </summary>
    public async Task<Dictionary<string, ValidTelemetryData[]>> QueryMultiAsync(
        string[] pointIds, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var ids = pointIds.Distinct(StringComparer.Ordinal).ToArray();
        // Decided before the lake scan, as QueryAsync does: a long scan must not age `end` out of
        // the lookback and drop the unflushed rows a request qualified for when it arrived.
        var mergeTail = TailMergePolicy.ShouldMergeTail(end, DateTime.UtcNow, _options.LookbackSec);
        var tailTask = mergeTail
            ? TryReadTailAsync(new HashSet<string>(ids, StringComparer.Ordinal), start, end, ct)
            : Task.FromResult(Array.Empty<ValidTelemetryData>());
        Dictionary<string, ValidTelemetryData[]> lake;
        if (_inner is IMultiPointTelemetryStore multi)
        {
            lake = await multi.QueryMultiAsync(ids, start, end, ct).ConfigureAwait(false);
        }
        else
        {
            lake = new Dictionary<string, ValidTelemetryData[]>(StringComparer.Ordinal);
            foreach (var id in ids) lake[id] = await _inner.QueryAsync(id, start, end, ct).ConfigureAwait(false);
        }

        // One stream scan for every point, split by point id afterwards.
        var tailByPoint = (await tailTask.ConfigureAwait(false))
            .Where(r => r.PointId is not null)
            .GroupBy(r => r.PointId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var result = new Dictionary<string, ValidTelemetryData[]>(ids.Length, StringComparer.Ordinal);
        foreach (var id in ids)
            result[id] = MergeTail(lake.GetValueOrDefault(id, []), tailByPoint.GetValueOrDefault(id, []));
        return result;
    }

    private static ValidTelemetryData[] MergeTail(ValidTelemetryData[] lakeRows, ValidTelemetryData[] tailRows)
    {
        if (tailRows.Length == 0) return lakeRows;

        // Merge: exclude tail rows whose (non-null) id already appears in the lake result.
        // Rows with Id == null can't be deduplicated by id — always include them from the tail
        // to avoid silently dropping un-identified readings.
        var lakeIds = new HashSet<string>(
            lakeRows.Select(r => r.Id).OfType<string>(),
            StringComparer.Ordinal);
        var merged = lakeRows.Concat(tailRows.Where(r => r.Id is null || !lakeIds.Contains(r.Id)))
            .OrderBy(r => TelemetryTimestamp.TryParseUtc(r.Datetime, out var t) ? t : DateTime.MinValue)
            .ToArray();

        BuildingOsMetrics.TailMergeRowsMerged.Add(tailRows.Length);
        return merged;
    }

    public Task<ValidTelemetryData?> QueryLatestAsync(string pointId, CancellationToken ct = default)
        => _inner.QueryLatestAsync(pointId, ct);

    private async Task<ValidTelemetryData[]> TryReadTailAsync(
        IReadOnlySet<string> pointIds, DateTime start, DateTime end, CancellationToken ct)
    {
        try
        {
            // Read JetStream from (end - lookback) clamped to start, not from start.
            // Reading from `start` (potentially days ago) would exhaust maxMsgs with old messages
            // before reaching the recent unflushed rows we actually care about.
            var tailSince = end.AddSeconds(-_options.LookbackSec);
            if (tailSince < start) tailSince = start;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.FetchTimeout);
            var rows = await _tailReader.ReadSinceAsync(
                tailSince, pointIds, _options.MaxMsgs, _options.FetchTimeout, timeout.Token).ConfigureAwait(false);
            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            BuildingOsMetrics.TailMergeErrors.Add(1);
            _logger.LogWarning(ex,
                "TailMergedTelemetryStore: tail fetch failed for {PointCount} point(s) (e.g. {PointId}); returning lake-only result (degraded)",
                pointIds.Count, pointIds.FirstOrDefault());
            return Array.Empty<ValidTelemetryData>();
        }
    }
}
