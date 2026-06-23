namespace BuildingOS.Shared.Infrastructure.Telemetry;

public interface IHotTelemetryStore
{
    Task PutAsync(string pointId, ValidTelemetryData data, CancellationToken cancellationToken = default);
    Task<ValidTelemetryData?> GetAsync(string pointId, CancellationToken cancellationToken = default);
}
