using System.Text.Json;

namespace BuildingOS.Shared;

/// <summary>
/// Pure mapping of a telemetry value's JSON representation onto the discriminated fields of
/// <see cref="ValidTelemetryData"/> (#152, ADR-0006). The <c>building-os.validated.telemetry</c> wire
/// keeps a single polymorphic <c>value</c> (number | string | boolean) — the discriminant is the JSON
/// kind — while the persistence/API layers split it into typed columns/fields. Numeric stays fully
/// backward-compatible.
/// </summary>
public static class TelemetryValueKind
{
    public const string Number = "number";
    public const string String = "string";
    public const string Boolean = "boolean";

    /// <summary>
    /// Populate <see cref="ValidTelemetryData.Value"/> / <c>ValueType</c> / <c>ValueText</c> /
    /// <c>ValueBool</c> from a JSON value element:
    /// <list type="bullet">
    /// <item>Number → <c>Value</c> (double), <c>ValueType = "number"</c></item>
    /// <item>String → <c>ValueText</c>, <c>ValueType = "string"</c></item>
    /// <item>True/False → <c>ValueBool</c>, <c>ValueType = "boolean"</c></item>
    /// </list>
    /// Any other kind (null / object / array / undefined) leaves all four unset — a non-representable
    /// value is dropped, matching the prior numeric-only behavior. Returns <c>true</c> when a value was
    /// applied.
    /// </summary>
    public static bool Apply(ValidTelemetryData target, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out var d):
                target.Value = d;
                target.ValueType = Number;
                return true;
            case JsonValueKind.String:
                target.ValueText = value.GetString();
                target.ValueType = String;
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                target.ValueBool = value.GetBoolean();
                target.ValueType = Boolean;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Project the stored discriminated fields back onto the single union-typed <c>value</c> the API
    /// returns (#344): a <see cref="double"/>, <see cref="string"/>, <see cref="bool"/>, or
    /// <c>null</c> when nothing is representable.
    /// <para>
    /// <b>Precedence is numeric first</b>, then <c>ValueText</c>, then <c>ValueBool</c> — and the
    /// discriminant does not override it. That is not a stylistic choice: an <i>aggregate</i> row
    /// legitimately carries two values at once. <c>AggregatingParquetTelemetryStore.ToTelemetry</c>
    /// sets <c>Value = Avg</c> unconditionally (the Timescale continuous-aggregate contract) while
    /// <see cref="ResolveLastInBucket"/> tags the bucket by its last-in-bucket reading, so a mixed
    /// hour is really <c>{ Value = 42, ValueType = "string", ValueText = "auto" }</c> on the wire.
    /// Collapsing that onto one <c>value</c> is lossy whichever half wins; the numeric one is chosen
    /// because <c>value</c> already has a published numeric meaning at Hour/Day granularity that
    /// charts and external consumers depend on, while the state representative stays reachable via
    /// <c>ValueText</c>/<c>ValueBool</c>.
    /// </para>
    /// <para>
    /// For a <i>raw</i> row the question does not arise: <see cref="Apply"/> populates exactly one
    /// payload field, so numeric-first and discriminant-first agree. The client resolver
    /// (<c>web-client/src/lib/telemetry/value.ts</c>) applies the same precedence; both are pinned by
    /// tests so they cannot drift.
    /// </para>
    /// </summary>
    public static object? Resolve(ValidTelemetryData? row) =>
        row is null ? null : row.Value ?? (object?)row.ValueText ?? row.ValueBool;

    /// <summary>
    /// The discriminant for a value produced by <see cref="Resolve"/> — i.e. the kind of the single
    /// union value the API ships, not the kind of whatever else the row carries beside it.
    /// <para>
    /// This exists because the stored <c>ValueType</c> cannot serve as the wire discriminant. For an
    /// aggregate row the store tags the bucket by its <i>last-in-bucket</i> reading while
    /// <c>Value</c> holds the average, so a mixed hour is stored as
    /// <c>{ Value = 42, ValueType = "string" }</c> — perfectly coherent for the store (the tag
    /// describes <c>ValueText</c>), and incoherent the moment it is copied onto a wire whose
    /// <c>value</c> is the number.
    /// </para>
    /// </summary>
    public static string? KindOf(object? value) => value switch
    {
        null => null,
        string => String,
        bool => Boolean,
        _ => Number,
    };

    /// <summary>
    /// Resolve an aggregated bucket's <b>last-in-bucket</b> discriminant (#152 Phase B, D3) from its
    /// latest row and whether the bucket contained any numeric value. A non-numeric latest row yields
    /// its string/boolean value; otherwise a bucket with numeric values is tagged <c>"number"</c> and
    /// an empty/non-representable bucket stays untagged (null).
    /// </summary>
    public static (string? ValueType, string? LastText, bool? LastBool) ResolveLastInBucket(
        ValidTelemetryData? last, bool hasNumeric) => last?.ValueType switch
    {
        String => (String, last.ValueText, (bool?)null),
        Boolean => (Boolean, null, last.ValueBool),
        _ => (hasNumeric ? Number : null, null, null),
    };

    /// <summary>
    /// True when (<paramref name="ts"/>, <paramref name="row"/>) is a more representative last-in-bucket
    /// candidate than the current best (<paramref name="bestTs"/>, <paramref name="best"/>): a strictly
    /// later timestamp, or the same timestamp with a greater <c>Id</c> (ordinal). The Id tiebreaker makes
    /// the pick order-independent even when two distinct readings share an identical timestamp (#152 D3).
    /// </summary>
    public static bool IsLaterInBucket(DateTime ts, ValidTelemetryData row, DateTime bestTs, ValidTelemetryData? best)
        => ts > bestTs
        || (ts == bestTs && string.CompareOrdinal(row.Id ?? string.Empty, best?.Id ?? string.Empty) > 0);
}
