using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Parquet serialization for rollup objects (#222). The discriminant columns
/// (<c>value_type</c>/<c>value_text</c>/<c>value_bool</c>, #152 Phase B) hold the non-numeric
/// last-in-bucket value and are <b>appended</b>, so old rollup objects (without them) read back as
/// numeric — the reader tolerates their absence.
/// </summary>
public static class RollupSerializer
{
    private static readonly DataField<string> PointId   = new("point_id");
    private static readonly DataField<string> Building  = new("building");
    private static readonly DataField<string> DeviceId  = new("device_id");
    private static readonly DataField<string> Name      = new("name");
    private static readonly DataField<double?> Avg      = new("avg");
    private static readonly DataField<double?> MinVal   = new("min_value");
    private static readonly DataField<double?> MaxVal   = new("max_value");
    private static readonly DataField<int> Count        = new("count");
    private static readonly DataField<DateTime?> HourUtc = new("hour_utc");
    private static readonly DataField<string> ValueType = new("value_type");
    private static readonly DataField<string> ValueText = new("value_text");
    private static readonly DataField<bool?> ValueBool  = new("value_bool");

    public static readonly ParquetSchema Schema =
        new(PointId, Building, DeviceId, Name, Avg, MinVal, MaxVal, Count, HourUtc, ValueType, ValueText, ValueBool);

    public static async Task WriteAsync(
        IReadOnlyList<RollupRow> rows, Stream stream, CancellationToken cancellationToken = default)
    {
        using var writer = await ParquetWriter.CreateAsync(Schema, stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        writer.CompressionMethod = CompressionMethod.Zstd;
        using var rg = writer.CreateRowGroup();

        await rg.WriteColumnAsync(new DataColumn(PointId, rows.Select(r => r.PointId).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(Building, rows.Select(r => r.Building).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(DeviceId, rows.Select(r => r.DeviceId).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(Name, rows.Select(r => r.Name).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(Avg, rows.Select(r => r.Avg).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(MinVal, rows.Select(r => r.MinValue).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(MaxVal, rows.Select(r => r.MaxValue).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(Count, rows.Select(r => r.Count).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(HourUtc, rows.Select(r => (DateTime?)r.HourUtc).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(ValueType, rows.Select(r => r.ValueType).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(ValueText, rows.Select(r => r.ValueText).ToArray())).ConfigureAwait(false);
        await rg.WriteColumnAsync(new DataColumn(ValueBool, rows.Select(r => r.ValueBool).ToArray())).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<RollupRow>> ReadAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var result = new List<RollupRow>();
        for (var rg = 0; rg < reader.RowGroupCount; rg++)
        {
            using var rgReader = reader.OpenRowGroupReader(rg);
            var cols = new Dictionary<string, Array>(StringComparer.Ordinal);
            foreach (var field in reader.Schema.GetDataFields())
            {
                var col = await rgReader.ReadColumnAsync(field).ConfigureAwait(false);
                cols[field.Name] = col.Data;
            }

            var n = cols.Values.First().Length;
            for (var i = 0; i < n; i++)
            {
                result.Add(new RollupRow(
                    cols.TryGetValue("point_id", out var pid) ? pid.GetValue(i)?.ToString() : null,
                    cols.TryGetValue("building", out var b) ? b.GetValue(i)?.ToString() : null,
                    cols.TryGetValue("device_id", out var d) ? d.GetValue(i)?.ToString() : null,
                    cols.TryGetValue("name", out var nm) ? nm.GetValue(i)?.ToString() : null,
                    cols.TryGetValue("avg", out var avg) ? avg.GetValue(i) is double da ? da : null : null,
                    cols.TryGetValue("min_value", out var mn) ? mn.GetValue(i) is double dm ? dm : null : null,
                    cols.TryGetValue("max_value", out var mx) ? mx.GetValue(i) is double dmax ? dmax : null : null,
                    cols.TryGetValue("count", out var cnt) ? cnt.GetValue(i) is int ic ? ic : 0 : 0,
                    ParseHour(cols, i),
                    // #152 Phase B — absent in old rollup objects (→ null → numeric).
                    cols.TryGetValue("value_type", out var vt) ? vt.GetValue(i)?.ToString() : null,
                    cols.TryGetValue("value_text", out var vx) ? vx.GetValue(i)?.ToString() : null,
                    cols.TryGetValue("value_bool", out var vb) && vb.GetValue(i) is bool bv ? bv : null));
            }
        }
        return result;
    }

    private static DateTime ParseHour(Dictionary<string, Array> cols, int i)
    {
        if (!cols.TryGetValue("hour_utc", out var h)) return default;
        var val = h.GetValue(i);
        if (val is DateTimeOffset dto) return dto.UtcDateTime;
        if (val is DateTime dt) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return default;
    }
}
