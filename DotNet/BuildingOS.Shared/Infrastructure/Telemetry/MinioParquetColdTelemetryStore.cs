using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Caching.Memory;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Cold-tier telemetry store backed by MinIO Parquet files (#212). Objects follow the writer layout
/// <c>building_id={b}/year={Y}/month={MM}/day={DD}/hour={HH}/part-*.parquet</c>
/// (see <see cref="ColdExport.NpgsqlMinioExportService"/>).
///
/// Because the building segment comes first, the time range cannot be pruned by a single prefix. The
/// reader discovers buildings (cached briefly), then lists only the month prefixes that overlap the
/// range and reads only the objects whose hour partition overlaps it. The listing/decoding primitives
/// live in <see cref="ParquetLakeScan"/>, shared with the parquet-mode warm+cold store (#214).
/// </summary>
public class MinioParquetColdTelemetryStore : IColdTelemetryStore
{
    private readonly ParquetLakeScan _scan;

    public MinioParquetColdTelemetryStore(IBlobStorage storage, IMemoryCache cache)
    {
        _scan = new ParquetLakeScan(storage, cache);
    }

    public async Task<ValidTelemetryData[]> QueryAsync(
        string pointId, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var keys = await _scan.ListKeysInRangeAsync(start, end, cancellationToken).ConfigureAwait(false);
        var rows = await _scan.ReadKeysAsync(keys, pointId, start, end, cancellationToken).ConfigureAwait(false);
        return rows.OrderBy(r => r.Datetime, StringComparer.Ordinal).ToArray();
    }
}
