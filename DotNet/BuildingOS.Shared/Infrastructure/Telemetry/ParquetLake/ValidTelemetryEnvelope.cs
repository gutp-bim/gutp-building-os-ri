using System.Text.Json;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Pure parser for the validated-telemetry envelope (#213). A message is
/// <c>{ "telemetries": [ { point_id, building, device_id, name, value, datetime, data, id } ] }</c>
/// (snake_case). Expands the array to flat <see cref="ValidTelemetryData"/> rows. Malformed messages
/// yield an empty list rather than throwing, so a single bad message never stops the writer.
/// </summary>
public static class ValidTelemetryEnvelope
{
    public static IReadOnlyList<ValidTelemetryData> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ValidTelemetryData>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("telemetries", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ValidTelemetryData>();
            }

            var rows = new List<ValidTelemetryData>(arr.GetArrayLength());
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var row = new ValidTelemetryData
                {
                    PointId  = GetString(el, "point_id"),
                    Building = GetString(el, "building"),
                    DeviceId = GetString(el, "device_id"),
                    Name     = GetString(el, "name"),
                    Datetime = GetString(el, "datetime"),
                    Data     = GetRaw(el, "data"),
                    Id       = GetString(el, "id"),
                };
                // Discriminated value (#152): the wire `value` is polymorphic; number → Value,
                // string → ValueText, boolean → ValueBool (non-representable kinds leave all unset).
                if (el.TryGetProperty("value", out var valueEl))
                {
                    TelemetryValueKind.Apply(row, valueEl);
                }
                rows.Add(row);
            }
            return rows;
        }
        catch (JsonException)
        {
            return Array.Empty<ValidTelemetryData>();
        }
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? GetRaw(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) &&
        v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? v.GetRawText()
            : null;
}
