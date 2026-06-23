namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>One pre-aggregated row in a rollup Parquet object (#222): avg/min/max/count over all raw rows for a single point in a single hour partition.</summary>
public sealed record RollupRow(
    string? PointId,
    string? Building,
    string? DeviceId,
    string? Name,
    double? Avg,
    double? MinValue,
    double? MaxValue,
    int Count,
    DateTime HourUtc);
