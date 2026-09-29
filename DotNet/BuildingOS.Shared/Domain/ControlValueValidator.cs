using System.Globalization;
using System.Text.Json;
using BuildingOS.Shared.Domain.TwinAdmin;

namespace BuildingOS.Shared.Domain;

/// <summary>Result of validating a control input value against a <see cref="ControlSchema"/>.</summary>
public readonly record struct ControlValidationResult(bool IsValid, string? Error)
{
    public static ControlValidationResult Ok { get; } = new(true, null);
    public static ControlValidationResult Invalid(string error) => new(false, error);
}

/// <summary>
/// Validates a control input value against the point's <see cref="ControlSchema"/> (#153). The schema
/// comes from the point list (source of truth). Validation is permissive where the schema cannot
/// constrain the value (unknown data type, enum without labels, number without bounds) — authorization
/// is handled separately by the writable gate (#139); this only rejects values the schema proves wrong.
/// </summary>
public static class ControlValueValidator
{
    public static ControlValidationResult Validate(ControlSchema schema, double value)
    {
        return (schema.DataType ?? string.Empty).ToLowerInvariant() switch
        {
            "boolean" => value is 0 or 1
                ? ControlValidationResult.Ok
                : ControlValidationResult.Invalid($"boolean control expects 0 or 1, got {Fmt(value)}"),
            "enum"    => ValidateEnum(schema, value),
            "number"  => ValidateNumber(schema, value),
            _         => ControlValidationResult.Ok, // unknown/unspecified type → cannot constrain
        };
    }

    /// <summary>
    /// Why <paramref name="schema"/> cannot constrain a control value, or null when it can (#481). The
    /// cases are exactly those where <see cref="Validate"/> is permissive for lack of a schema to check
    /// against — no schema, no or an unknown data type, an enum without a usable labels map — so a
    /// fail-closed policy refuses what would otherwise be waved through. A number without bounds is
    /// usable: bounds are optional, and the data type is still enforced.
    /// </summary>
    public static string? UnusableReason(ControlSchema? schema)
    {
        if (schema is null) return ControlSchemaIssueReasons.NoSchema;
        var dataType = schema.DataType?.Trim().ToLowerInvariant();
        return dataType switch
        {
            null or "" => ControlSchemaIssueReasons.MissingDataType,
            "boolean" or "number" => null,
            "enum" => ParseAllowedCodes(schema.EnumLabels) is { Count: > 0 }
                ? null
                : ControlSchemaIssueReasons.MalformedEnumLabels,
            _ => ControlSchemaIssueReasons.UnknownDataType,
        };
    }

    private static ControlValidationResult ValidateEnum(ControlSchema schema, double value)
    {
        var allowed = ParseAllowedCodes(schema.EnumLabels);
        // Without a labels map there is no allowed set to check against → do not block.
        if (allowed is null || allowed.Count == 0) return ControlValidationResult.Ok;

        return allowed.Contains(value)
            ? ControlValidationResult.Ok
            : ControlValidationResult.Invalid(
                // Sort so the error message is deterministic (HashSet enumeration order is not).
                $"enum control value {Fmt(value)} is not one of [{string.Join(", ", allowed.OrderBy(c => c).Select(Fmt))}]");
    }

    private static ControlValidationResult ValidateNumber(ControlSchema schema, double value)
    {
        if (schema.MinValue is { } min && value < min)
            return ControlValidationResult.Invalid($"value {Fmt(value)} is below the minimum {Fmt(min)}");
        if (schema.MaxValue is { } max && value > max)
            return ControlValidationResult.Invalid($"value {Fmt(value)} is above the maximum {Fmt(max)}");
        return ControlValidationResult.Ok;
    }

    // EnumLabels is a JSON object { "1": "冷房", ... } whose keys are the allowed numeric codes.
    private static HashSet<double>? ParseAllowedCodes(string? enumLabels)
    {
        if (string.IsNullOrWhiteSpace(enumLabels)) return null;
        try
        {
            using var doc = JsonDocument.Parse(enumLabels);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var codes = new HashSet<double>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (double.TryParse(prop.Name, NumberStyles.Float, CultureInfo.InvariantCulture, out var code))
                    codes.Add(code);
            }
            return codes;
        }
        catch (JsonException)
        {
            return null; // malformed labels → cannot derive an allowed set → permissive
        }
    }

    private static string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);
}
