using BuildingOS.Shared;

namespace BuildingOs.ApiServer.Telemetry;

/// <summary>
/// Response-only wire DTO for telemetry reads (#344).
///
/// <para>
/// Until now the controllers returned <see cref="ValidTelemetryData"/> directly, so the storage
/// layer's discriminated split (<c>value</c>/<c>valueType</c>/<c>valueText</c>/<c>valueBool</c>,
/// #152) leaked into the HTTP contract and every API consumer had to reassemble it. That split is a
/// Parquet/EF concern — <see cref="ValidTelemetryData"/> is an EF entity and binds the lake's column
/// model, so it cannot be retyped in place — while the canonical schema
/// (<c>Defines/Schemas/valid-message.json</c>) and the NATS bus have always carried one polymorphic
/// <c>value</c>. This DTO restores that shape at the boundary.
/// </para>
///
/// <para>
/// <b>#359 removed the legacy payload fields.</b> <c>valueText</c>/<c>valueBool</c> are gone; the
/// non-numeric half of a reading now travels in <see cref="State"/>. <see cref="ValueType"/> stays,
/// because it never duplicated the payload the way those two did — it describes <see cref="Value"/>,
/// derived from the value actually shipped rather than copied from the stored tag. (Copying the
/// stored tag is what made the wire say <c>{ value: 42, valueType: "string" }</c> for a mixed
/// aggregate bucket, where the stored tag classifies the bucket's last-in-bucket reading.)
/// </para>
/// </summary>
/// <param name="Value">
/// The reading, as a <see cref="double"/>, <see cref="string"/>, <see cref="bool"/>, or <c>null</c>.
/// Declared <c>object?</c> so System.Text.Json writes it by its runtime type; the OpenAPI schema is
/// widened to <c>oneOf: [number, string, boolean]</c> by <c>TelemetryValueSchemaFilter</c>, which is
/// what makes the generated clients see a real union rather than an untyped hole.
/// <para>
/// <b>Response-only.</b> Round-tripping this record through a request body would deserialize
/// <c>Value</c> as a <c>JsonElement</c>, not the original primitive. Do not reuse it for input or as
/// a NATS payload without adding a converter.
/// </para>
/// </param>
/// <param name="State">
/// The reading's <b>non-numeric half</b> — a <see cref="string"/>, a <see cref="bool"/>, or
/// <c>null</c> — independent of any number in <paramref name="Value"/> (#359).
/// <para>
/// It exists for the one row shape that carries two readings at once: an aggregate bucket sets
/// <paramref name="Value"/> to the average unconditionally (the continuous-aggregate contract), so a
/// mixed hour ships the number while its last-in-bucket state has nowhere else to live. That is what
/// the state timeline reads at Hour/Day granularity.
/// </para>
/// <para>
/// A raw non-numeric row <b>repeats</b> its reading here rather than leaving this null. The
/// duplication is deliberate: it makes this field mean one thing unconditionally, so a client reads
/// it with a single lookup instead of falling back to <paramref name="Value"/> — the fallback chain
/// #359 exists to delete. Widened to <c>oneOf: [string, boolean]</c> by
/// <c>TelemetryValueSchemaFilter</c>, for the same reason <paramref name="Value"/> is.
/// </para>
/// </param>
public sealed record TelemetryReading(
    string? PointId,
    string? Datetime,
    object? Value,
    string? Building = null,
    string? DeviceId = null,
    string? Name = null,
    string? Data = null,
    string? Id = null,
    string? ValueType = null,
    object? State = null)
{
    /// <summary>Projects a stored row onto the wire shape. Null in, null out.</summary>
    public static TelemetryReading? From(ValidTelemetryData? row)
    {
        if (row is null) return null;

        var value = TelemetryValueKind.Resolve(row);
        return new TelemetryReading(
            row.PointId,
            row.Datetime,
            value,
            row.Building,
            row.DeviceId,
            row.Name,
            row.Data,
            row.Id,
            // Derived from the value actually shipped, NOT copied from the row: the stored ValueType
            // tags an aggregate bucket by its last-in-bucket reading, so passing it through made the
            // wire say `{ value: 42, valueType: "string" }`.
            TelemetryValueKind.KindOf(value),
            TelemetryValueKind.ResolveState(row));
    }

    /// <summary>Projects a result set, preserving order. Null rows are dropped.</summary>
    public static TelemetryReading[] From(IEnumerable<ValidTelemetryData>? rows) =>
        rows is null
            ? []
            : rows.Select(From).OfType<TelemetryReading>().ToArray();
}
