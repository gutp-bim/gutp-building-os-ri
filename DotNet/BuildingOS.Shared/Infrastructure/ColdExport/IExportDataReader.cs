namespace BuildingOS.Shared.Infrastructure.ColdExport;

public interface IExportDataReader
{
    Task<ValidTelemetryData[]> ReadAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
    Task<DateTime?> GetLastExportEndAsync(CancellationToken cancellationToken = default);
}
