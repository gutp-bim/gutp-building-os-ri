using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.ColdExport;

/// <summary>
/// Background worker that periodically exports telemetry from TimescaleDB to MinIO Parquet.
/// Interval controlled by COLD_EXPORT_INTERVAL env var (minutes, 1-15, default 5).
/// </summary>
public sealed class ColdExportWorker : BackgroundService
{
    private readonly IColdExportService _service;
    private readonly ILogger<ColdExportWorker> _logger;
    private readonly TimeSpan _interval;

    public ColdExportWorker(
        IColdExportService service,
        ILogger<ColdExportWorker> logger,
        int intervalMinutes = 5)
    {
        _service = service;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 1, 15));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ColdExportWorker started (interval={Interval})", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var to = DateTime.UtcNow.AddMinutes(-1); // 1 min buffer for in-flight messages
            var from = to - _interval;

            await ExportOnceAsync(from, to, stoppingToken);

            await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Internal export step — exposed for testability.</summary>
    public async Task ExportOnceAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
            var result = await _service.ExportChunkAsync(from, to, ct);
            BuildingOsMetrics.ColdExportRows.Add(result.RowsExported);
            if (result.RowsExported > 0)
                _logger.LogInformation(
                    "ColdExport: exported {Rows} rows → {Path} (verified={Verified})",
                    result.RowsExported, result.ParquetPath, result.Verified);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BuildingOsMetrics.ColdExportFailures.Add(1);
            _logger.LogError(ex, "ColdExportWorker: export failed for [{From}, {To})", from, to);
        }
    }
}
