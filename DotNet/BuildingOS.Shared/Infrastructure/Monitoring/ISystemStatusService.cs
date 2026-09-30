using BuildingOS.Shared.Domain.Configuration;

namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>
/// Aggregates per-service up/down state and a few operational KPIs into a single
/// <see cref="SystemStatus"/> for the built-in simple-monitoring view.
/// </summary>
public interface ISystemStatusService
{
    /// <param name="thresholds">Effective pipeline KPI thresholds; the Parquet flush-stall window is
    /// max(<see cref="PipelineKpiThresholds.ParquetFreshnessWarnSeconds"/>, 15m).</param>
    Task<SystemStatus> GetStatusAsync(PipelineKpiThresholds thresholds, CancellationToken ct);
}
