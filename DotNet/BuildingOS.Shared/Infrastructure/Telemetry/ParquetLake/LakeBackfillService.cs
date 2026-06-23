using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.ColdExport;
using Microsoft.Extensions.Caching.Memory;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>Outcome of a backfill run — used for the row-count reconciliation output (#218).</summary>
public sealed record BackfillResult(long RowsRead, long RowsWritten, int ObjectsWritten, int HoursProcessed)
{
    public static readonly BackfillResult Empty = new(0, 0, 0, 0);
    public BackfillResult Add(BackfillResult other) => new(
        RowsRead + other.RowsRead, RowsWritten + other.RowsWritten,
        ObjectsWritten + other.ObjectsWritten, HoursProcessed + other.HoursProcessed);
}

/// <summary>
/// Migrates existing TimescaleDB <c>telemetry</c> rows into the Parquet lake (#218): for each hour
/// window in the range it reads the rows, groups them by building, de-duplicates by id, and writes one
/// deterministic <c>part-backfill-*.parquet</c> per building-hour. The deterministic key makes re-runs
/// idempotent (overwrite, no duplicate objects) and read-time id-dedup (#214) means the backfilled rows
/// coexist with the live writer's parts until compaction (#217) merges them. Reports rows read vs.
/// written for reconciliation. New OSS deployments never need this (they start in parquet mode).
/// </summary>
public sealed class LakeBackfillService
{
    private readonly IExportDataReader _reader;
    private readonly ParquetLakeScan _scan;

    public LakeBackfillService(IExportDataReader reader, IBlobStorage storage, IMemoryCache cache)
    {
        _reader = reader;
        _scan = new ParquetLakeScan(storage, cache);
    }

    public async Task<BackfillResult> RunAsync(
        DateTime fromUtc, DateTime toUtc, string? buildingFilter = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var total = BackfillResult.Empty;
        foreach (var window in BackfillPlanner.HourWindows(fromUtc, toUtc))
        {
            ct.ThrowIfCancellationRequested();
            var hourResult = await BackfillHourAsync(window, buildingFilter, progress, ct).ConfigureAwait(false);
            total = total.Add(hourResult);
        }
        progress?.Invoke(
            $"Backfill complete: {total.HoursProcessed} hours, {total.RowsRead} rows read, " +
            $"{total.RowsWritten} written to {total.ObjectsWritten} objects.");
        return total;
    }

    private async Task<BackfillResult> BackfillHourAsync(
        BackfillWindow window, string? buildingFilter, Action<string>? progress, CancellationToken ct)
    {
        var rows = await _reader.ReadAsync(window.ReadFromUtc, window.ReadToUtc, ct).ConfigureAwait(false);

        // Apply the building filter up front so the reconciliation counts (RowsRead vs RowsWritten)
        // compare like with like — RowsRead must not include rows for buildings we never write.
        var selected = buildingFilter is null
            ? rows
            : rows.Where(r => string.Equals(r.Building, buildingFilter, StringComparison.Ordinal)).ToArray();
        if (selected.Length == 0)
        {
            return new BackfillResult(0, 0, 0, 1);
        }

        // Group by building (rows in this window all belong to window.HourUtc's partition).
        var byBuilding = selected.GroupBy(r => string.IsNullOrEmpty(r.Building) ? "unknown" : r.Building!);

        long written = 0;
        var objects = 0;
        foreach (var group in byBuilding)
        {
            var deduped = ParquetLakeReadPlanner.DedupById(group);
            var key = BackfillPlanner.BackfillKey(group.Key, window.HourUtc);
            await _scan.WriteObjectAsync(key, deduped, ct).ConfigureAwait(false);
            written += deduped.Length;
            objects++;
            progress?.Invoke($"  {window.HourUtc:yyyy-MM-ddTHH}:00 {group.Key}: {deduped.Length} rows → {key}");
        }

        return new BackfillResult(selected.Length, written, objects, 1);
    }
}
