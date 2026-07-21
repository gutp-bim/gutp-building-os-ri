namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// One pre-aggregated row in a rollup Parquet object (#222): avg/min/max/count over all raw rows for a
/// single point in a single hour partition. For a non-numeric point (#152 Phase B) the numeric
/// aggregates are null and the row carries the <b>last-in-bucket</b> representative value
/// (<c>ValueType</c> "string"/"boolean" with <c>ValueText</c>/<c>ValueBool</c>). The discriminant
/// fields are appended so old rollup objects (without them) read back as numeric.
/// </summary>
public sealed record RollupRow(
    string? PointId,
    string? Building,
    string? DeviceId,
    string? Name,
    double? Avg,
    double? MinValue,
    double? MaxValue,
    int Count,
    DateTime HourUtc,
    string? ValueType = null,
    string? ValueText = null,
    bool? ValueBool = null);
