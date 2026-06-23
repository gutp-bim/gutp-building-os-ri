namespace BuildingOS.Shared.Infrastructure.ColdExport;

public interface IColdExportService
{
    Task<ColdExportResult> ExportChunkAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
}
