namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Optional capability for a telemetry store that can resolve several points in a single backing scan
/// (#215). The Parquet lake reads each object once and filters all requested point ids, avoiding the
/// N-times-the-IO of looping per point. Stores that do not implement it fall back to per-point queries.
/// </summary>
public interface IMultiPointTelemetryStore
{
    Task<Dictionary<string, ValidTelemetryData[]>> QueryMultiAsync(
        string[] pointIds, DateTime start, DateTime end, CancellationToken cancellationToken = default);
}
