using BuildingOS.Shared.Infrastructure.BlobStorage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>Configuration for the <see cref="CompactionWorker"/> (#217).</summary>
public sealed record CompactionWorkerOptions
{
    /// <summary>How often the compactor scans the lake for settled hour partitions.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Grace after an hour ends before it is considered settled (no more parts will land).</summary>
    public TimeSpan SettleGrace { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Minimum fresh parts in a settled hour before compacting it.</summary>
    public int MinParts { get; init; } = 2;
}

/// <summary>
/// Merges the many small <c>part-*.parquet</c> objects that the 5–15 min flushes leave per building-hour
/// into one <c>compact-*.parquet</c> (#217), bounding the per-query object count. For each settled hour
/// (<see cref="CompactionPlanner"/>): read all source rows → de-duplicate by id → write the compact
/// object → verify the round-trip row count → delete the source parts. The write is an idempotent
/// overwrite of a deterministic key and the parts are deleted only after a successful verify, so an
/// interrupted run re-converges on the next cycle without loss or duplication. The reader already
/// prefers the compact object over the parts (#214), so queries are unaffected mid-compaction.
/// </summary>
public sealed class CompactionWorker : BackgroundService
{
    private readonly ParquetLakeScan _scan;
    private readonly ILogger<CompactionWorker> _logger;
    private readonly CompactionWorkerOptions _options;

    public CompactionWorker(
        IBlobStorage storage, IMemoryCache cache, ILogger<CompactionWorker> logger, CompactionWorkerOptions options)
    {
        _scan = new ParquetLakeScan(storage, cache);
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "CompactionWorker started (interval={Interval}, settleGrace={Grace}, minParts={MinParts})",
            _options.Interval, _options.SettleGrace, _options.MinParts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CompactionWorker: cycle error");
            }

            try { await Task.Delay(_options.Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One compaction pass: plan settled targets and compact each. Internal for tests.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var keys = await _scan.ListAllKeysAsync(ct).ConfigureAwait(false);
        var targets = CompactionPlanner.Plan(keys, DateTime.UtcNow, _options.SettleGrace, _options.MinParts);
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            await CompactAsync(target, ct).ConfigureAwait(false);
        }
    }

    private async Task CompactAsync(CompactionTarget target, CancellationToken ct)
    {
        try
        {
            var rows = await _scan.ReadAllRowsAsync(target.SourceKeys, ct).ConfigureAwait(false);
            var deduped = ParquetLakeReadPlanner.DedupById(rows);

            // Idempotent overwrite of the deterministic compact key.
            await _scan.WriteObjectAsync(target.CompactKey, deduped, ct).ConfigureAwait(false);

            // Verify the written object round-trips before deleting any source — never lose data on a
            // bad write; the parts stay and the next cycle retries.
            var check = await _scan.ReadAllRowsAsync(new[] { target.CompactKey }, ct).ConfigureAwait(false);
            if (check.Count != deduped.Length)
            {
                BuildingOsMetrics.CompactionFailures.Add(1);
                _logger.LogError(
                    "CompactionWorker: verify mismatch for {Key} (wrote {Expected}, read {Actual}); keeping parts",
                    target.CompactKey, deduped.Length, check.Count);
                return;
            }

            // Delete the source parts only — never the compact key we just wrote (it may have been one of
            // the sources when re-compacting).
            var partsToDelete = target.SourceKeys
                .Where(k => !string.Equals(k, target.CompactKey, StringComparison.Ordinal))
                .ToList();
            foreach (var part in partsToDelete)
            {
                await _scan.DeleteAsync(part, ct).ConfigureAwait(false);
            }

            // Record compaction success before the best-effort rollup write so that an OCE from
            // WriteRollupAsync does not skip these metrics (the compaction itself succeeded).
            BuildingOsMetrics.CompactionPartitions.Add(1);
            BuildingOsMetrics.CompactionPartsDeleted.Add(partsToDelete.Count);
            _logger.LogInformation(
                "CompactionWorker: compacted {Building} {Hour:yyyy-MM-ddTHH}:00 → {Key} ({Rows} rows, {Parts} parts merged)",
                target.Building, target.HourUtc, target.CompactKey, deduped.Length, partsToDelete.Count);

            // Write pre-aggregated rollup for fast aggregate queries (#222). Failure does NOT abort
            // the compaction — the rollup is best-effort; missing rollups fall back to aggregate-on-read.
            await WriteRollupAsync(target, deduped, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BuildingOsMetrics.CompactionFailures.Add(1);
            _logger.LogError(ex, "CompactionWorker: failed compacting {Key}; parts retained for retry", target.CompactKey);
        }
    }

    private async Task WriteRollupAsync(CompactionTarget target, ValidTelemetryData[] deduped, CancellationToken ct)
    {
        try
        {
            var rollupRows = RollupAggregator.Compute(deduped, target.HourUtc);
            var rollupKey  = RollupPartitionKey.AggKey(target.Building, target.HourUtc);
            using var ms = new MemoryStream();
            await RollupSerializer.WriteAsync(rollupRows, ms, ct).ConfigureAwait(false);
            ms.Position = 0;
            await _scan.WriteRawAsync(rollupKey, ms, ct).ConfigureAwait(false);

            BuildingOsMetrics.CompactionRollupsWritten.Add(1);
            _logger.LogDebug(
                "CompactionWorker: rollup written {Key} ({Points} points)", rollupKey, rollupRows.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BuildingOsMetrics.CompactionRollupFailures.Add(1);
            _logger.LogWarning(ex, "CompactionWorker: rollup write failed for {Building} {Hour:yyyy-MM-ddTHH}; continuing (fallback to aggregate-on-read)",
                target.Building, target.HourUtc);
        }
    }
}
