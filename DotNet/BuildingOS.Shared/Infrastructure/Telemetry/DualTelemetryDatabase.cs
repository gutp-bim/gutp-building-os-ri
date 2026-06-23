namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Dual-mode ITelemetryDatabase: reads from primary (CosmosDB / Azure).
/// In BUILDING_OS_BACKEND=dual, both primary and secondary are registered
/// in DI, and parity metrics are collected separately by ParityCheckJob.
///
/// Failure semantics: primary (CosmosDB) is authoritative.
/// Secondary (TimescaleDB) failures are logged but do NOT propagate to callers.
/// This ensures zero regression risk during the migration window.
/// </summary>
public class DualTelemetryDatabase : ITelemetryDatabase
{
    private readonly ITelemetryDatabase _primary;
    private readonly ITelemetryDatabase _secondary;

    public DualTelemetryDatabase(ITelemetryDatabase primary, ITelemetryDatabase secondary)
    {
        _primary = primary;
        _secondary = secondary;
    }

    public Task<ValidTelemetryData[]> GetWarmTelemetries(string pointId, DateTime startTime, DateTime endTime)
        => _primary.GetWarmTelemetries(pointId, startTime, endTime);

    public Task<ValidTelemetryData[]> GetColdTelemetries(string pointId, DateTime startTime, DateTime endTime)
        => _primary.GetColdTelemetries(pointId, startTime, endTime);

    public Task<Dictionary<string, ValidTelemetryData[]>> GetColdTelemetries(string[] pointIds, DateTime startTime, DateTime endTime)
        => _primary.GetColdTelemetries(pointIds, startTime, endTime);

    public Task<ValidTelemetryData?> GetHotTelemetry(string pointId)
        => _primary.GetHotTelemetry(pointId);

    /// <summary>
    /// Queries the secondary database for parity comparison.
    /// Called by ParityCheckJob, not the normal read path.
    /// </summary>
    public Task<ValidTelemetryData[]> GetSecondaryWarmTelemetries(string pointId, DateTime startTime, DateTime endTime)
        => _secondary.GetWarmTelemetries(pointId, startTime, endTime);
}
