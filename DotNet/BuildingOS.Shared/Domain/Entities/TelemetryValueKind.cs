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
}
