namespace BuildingOS.Shared.Infrastructure.Telemetry;

public interface IColdTelemetryStore
{
    Task<ValidTelemetryData[]> QueryAsync(string pointId, DateTime start, DateTime end, CancellationToken cancellationToken = default);
}
