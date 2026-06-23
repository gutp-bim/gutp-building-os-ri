namespace BuildingOS.Shared.Infrastructure.Telemetry;

public interface IAggregatedTelemetryStore
{
    Task<ValidTelemetryData[]> QueryHourlyAsync(
        string pointId, DateTime start, DateTime end,
        CancellationToken cancellationToken = default);

    Task<ValidTelemetryData[]> QueryDailyAsync(
        string pointId, DateTime start, DateTime end,
        CancellationToken cancellationToken = default);
}
