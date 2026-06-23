namespace BuildingOS.Shared.Infrastructure.Telemetry;

public interface IWarmTelemetryStore
{
    Task<ValidTelemetryData[]> QueryAsync(string pointId, DateTime start, DateTime end, CancellationToken cancellationToken = default);
    Task<ValidTelemetryData?> QueryLatestAsync(string pointId, CancellationToken cancellationToken = default);
}
