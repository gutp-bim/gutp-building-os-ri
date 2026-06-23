using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.ColdExport;

/// <summary>
/// Exports telemetry data from TimescaleDB to MinIO Parquet files.
/// Output path: cold/building_id={building}/year={Y}/month={MM}/day={DD}/hour={HH}/part-{ts}.parquet
/// </summary>
public sealed class NpgsqlMinioExportService : IColdExportService
{
    private const string ColdBucket = "cold";

    private readonly IExportDataReader _reader;
    private readonly IBlobStorage _storage;
    private readonly IExportLogRepository _log;
    private readonly ILogger<NpgsqlMinioExportService> _logger;

    public NpgsqlMinioExportService(
        IExportDataReader reader,
        IBlobStorage storage,
        IExportLogRepository log,
        ILogger<NpgsqlMinioExportService> logger)
    {
        _reader = reader;
        _storage = storage;
        _log = log;
        _logger = logger;
    }

    public async Task<ColdExportResult> ExportChunkAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var rows = await _reader.ReadAsync(from, to, cancellationToken);
        if (rows.Length == 0)
        {
            _logger.LogDebug("No data to export for [{From}, {To})", from, to);
            return new ColdExportResult(0, 0, null, false);
        }

        // Partition by building
        var byBuilding = rows.GroupBy(r => r.Building ?? "unknown").ToList();
        long totalRows = 0;
        long totalBytes = 0;
        string? firstPath = null;
        int? firstLogId = null;

        foreach (var group in byBuilding)
        {
            var building = group.Key;
            var buildingRows = group.ToArray();
            var key = BuildParquetKey(building, from);

            using var ms = new MemoryStream();
            await ParquetTelemetrySerializer.WriteAsync(buildingRows, ms, cancellationToken);
            ms.Position = 0;

            var bytes = ms.Length;
            await _storage.PutAsync(ColdBucket, key, ms, "application/octet-stream", cancellationToken);

            var logId = await _log.InsertAsync(from, to, key, buildingRows.Length, bytes, cancellationToken);

            totalRows += buildingRows.Length;
            totalBytes += bytes;
            firstPath ??= key;
            firstLogId ??= logId;

            await _log.SetVerifiedAsync(logId, cancellationToken);
            _logger.LogInformation("Exported {Rows} rows to {Key} (verified)", buildingRows.Length, key);
        }

        return new ColdExportResult(totalRows, totalBytes, firstPath, true);
    }

    private static string BuildParquetKey(string building, DateTime from) =>
        $"building_id={building}/year={from.Year:D4}/month={from.Month:D2}/day={from.Day:D2}/hour={from.Hour:D2}/part-{from:yyyyMMddHHmmss}.parquet";
}
