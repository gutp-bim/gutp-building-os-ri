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
/// <b>Dual-emitting for this release.</b> <see cref="ValueText"/>/<see cref="ValueBool"/> are still
/// populated alongside the union so a client built against the old shape keeps working regardless of
/// deploy order. Dropping them is a follow-up at a release boundary (#344 PR B); doing it now would
/// force a clients-first deploy for no benefit.
/// <para>
/// <see cref="ValueType"/> is <b>not</b> part of that promise: it now describes <see cref="Value"/>,
/// derived from the value actually shipped, rather than being copied from the stored tag. For an
/// aggregate bucket the stored tag classifies the bucket's last-in-bucket reading, so copying it
/// made the wire say <c>{ value: 42, valueType: "string" }</c>. An old client that branched on the
/// discriminant to render a mixed aggregate hour therefore sees the average now instead of the
/// state string — the state itself is unchanged and still in <see cref="ValueText"/>.
/// </para>
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
    string? ValueText = null,
    bool? ValueBool = null)
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
            row.ValueText,
            row.ValueBool);
    }

    /// <summary>Projects a result set, preserving order. Null rows are dropped.</summary>
    public static TelemetryReading[] From(IEnumerable<ValidTelemetryData>? rows) =>
        rows is null
            ? []
            : rows.Select(From).OfType<TelemetryReading>().ToArray();
}
