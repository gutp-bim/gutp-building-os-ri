using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Canonical Parquet serialization for telemetry rows (#213). The 8-column schema and Zstd compression
/// are the single source of truth shared by the cold export service and the Parquet-lake writer, so
/// objects written by either are readable by <see cref="MinioParquetColdTelemetryStore"/>.
/// </summary>
public static class ParquetTelemetrySerializer
{
    private static readonly DataField<string> PointId = new("point_id");
    private static readonly DataField<string> Building = new("building");
    private static readonly DataField<string> DeviceId = new("device_id");
    private static readonly DataField<string> Name = new("name");
    private static readonly DataField<double?> Value = new("value");
    private static readonly DataField<DateTime?> Time = new("time");
    private static readonly DataField<string> Data = new("data");
    private static readonly DataField<string> Id = new("id");

    public static readonly ParquetSchema Schema = new(PointId, Building, DeviceId, Name, Value, Time, Data, Id);

    /// <summary>Writes <paramref name="rows"/> as a single row group (Zstd) to <paramref name="stream"/>.</summary>
    public static async Task WriteAsync(
        IReadOnlyList<ValidTelemetryData> rows, Stream stream, CancellationToken cancellationToken = default)
    {
        using var writer = await ParquetWriter.CreateAsync(Schema, stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        writer.CompressionMethod = CompressionMethod.Zstd;
        using var rg = writer.CreateRowGroup();

        await rg.WriteColumnAsync(new DataColumn(PointId, rows.Select(r => r.PointId).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(Building, rows.Select(r => r.Building).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(DeviceId, rows.Select(r => r.DeviceId).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(Name, rows.Select(r => r.Name).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(Value, rows.Select(r => r.Value).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(Time, rows.Select(ParseTime).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(Data, rows.Select(r => r.Data).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(Id, rows.Select(r => r.Id).ToArray())).ConfigureAwait(false);
    }

    // Same parser as the partition-hour computation, so the time column and the partition agree.
    private static DateTime? ParseTime(ValidTelemetryData r) =>
        TelemetryTimestamp.TryParseUtc(r.Datetime, out var utc) ? utc : null;
}
