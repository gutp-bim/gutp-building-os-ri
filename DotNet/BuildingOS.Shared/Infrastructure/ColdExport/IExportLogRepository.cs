namespace BuildingOS.Shared.Infrastructure.ColdExport;

public interface IExportLogRepository
{
    Task<int> InsertAsync(DateTime from, DateTime to, string path, long rows, long bytes, CancellationToken cancellationToken = default);
    Task SetVerifiedAsync(int id, CancellationToken cancellationToken = default);
}
