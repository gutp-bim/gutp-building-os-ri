namespace BuildingOS.Shared.Infrastructure.Telemetry;

public interface ITelemetryQueryRouter
{
    Task<ValidTelemetryData[]> QueryAsync(
        TelemetryQueryRequest request,
        CancellationToken cancellationToken = default);
}
