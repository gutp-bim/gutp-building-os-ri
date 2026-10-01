namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// A router that can answer a raw range read for many points in one pass over the stores (#510), so a
/// batch reads each lake object once instead of once per point. Optional: callers fall back to
/// per-point <see cref="ITelemetryQueryRouter.QueryAsync"/> when a router does not implement it.
/// </summary>
public interface IMultiPointTelemetryQueryRouter
{
    /// <summary>
    /// Raw rows for each of <paramref name="pointIds"/> in [<paramref name="start"/>, <paramref name="end"/>],
    /// with the same tier selection as a raw <see cref="ITelemetryQueryRouter.QueryAsync"/>. Every
    /// requested id has an entry (empty when it has no rows).
    /// </summary>
    Task<Dictionary<string, ValidTelemetryData[]>> QueryRawMultiAsync(
        string[] pointIds, DateTime start, DateTime end, CancellationToken cancellationToken = default);
}
