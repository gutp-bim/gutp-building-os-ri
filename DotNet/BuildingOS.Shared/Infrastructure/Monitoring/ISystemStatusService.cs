namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>
/// Aggregates per-service up/down state and a few operational KPIs into a single
/// <see cref="SystemStatus"/> for the built-in simple-monitoring view.
/// </summary>
public interface ISystemStatusService
{
    Task<SystemStatus> GetStatusAsync(CancellationToken ct);
}
