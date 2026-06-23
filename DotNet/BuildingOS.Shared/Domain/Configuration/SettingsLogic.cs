using System.Globalization;

namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>Result of validating a candidate value against a <see cref="SettingDefinition"/> (#148).</summary>
public readonly record struct SettingValidationResult(bool IsValid, string? Error, string? Normalized)
{
    public static SettingValidationResult Ok(string normalized) => new(true, null, normalized);
    public static SettingValidationResult Fail(string error) => new(false, error, null);
}

/// <summary>
/// Pure validation + merge logic for editable app settings (#148). Kept free of EF/IO so it is fully
/// unit-testable.
/// </summary>
public static class SettingsLogic
{
    /// <summary>
    /// Validates <paramref name="value"/> against the definition's type and returns a normalized form
    /// (lower-case <c>true/false</c> for booleans, invariant-culture number for numbers). Strings pass
    /// through unchanged.
    /// </summary>
    public static SettingValidationResult Validate(SettingDefinition definition, string? value)
    {
        var raw = value ?? string.Empty;
        switch (definition.Type)
        {
            case SettingType.Boolean:
                if (bool.TryParse(raw, out var b))
                {
                    return SettingValidationResult.Ok(b ? "true" : "false");
                }
                return SettingValidationResult.Fail("真偽値（true/false）が必要です");

            case SettingType.Number:
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    return SettingValidationResult.Ok(n.ToString(CultureInfo.InvariantCulture));
                }
                return SettingValidationResult.Fail("数値が必要です");

            case SettingType.String:
            default:
                return SettingValidationResult.Ok(raw);
        }
    }

    /// <summary>
    /// Merges a definition with an optional stored override into a <see cref="SettingView"/>. With no
    /// override the effective value is the default and the source is <see cref="SettingSource.Default"/>.
    /// </summary>
    public static SettingView Merge(SettingDefinition definition, SettingOverride? overrideEntry)
    {
        if (overrideEntry is null)
        {
            return new SettingView(
                definition.Key,
                definition.Type,
                definition.Description,
                definition.Category,
                Value: definition.DefaultValue,
                DefaultValue: definition.DefaultValue,
                IsOverridden: false,
                Source: SettingSource.Default,
                UpdatedAt: null,
                UpdatedBy: null);
        }

        return new SettingView(
            definition.Key,
            definition.Type,
            definition.Description,
            definition.Category,
            Value: overrideEntry.Value,
            DefaultValue: definition.DefaultValue,
            IsOverridden: true,
            Source: overrideEntry.Source,
            UpdatedAt: overrideEntry.UpdatedAt,
            UpdatedBy: overrideEntry.UpdatedBy);
    }

    /// <summary>
    /// Builds the full effective settings list: every allowlisted definition merged with its override
    /// (if any). Overrides whose key is not in the registry are ignored (stale rows never surface).
    /// </summary>
    public static IReadOnlyList<SettingView> BuildViews(
        IEnumerable<SettingDefinition> definitions,
        IReadOnlyDictionary<string, SettingOverride> overrides) =>
        definitions
            .Select(def => Merge(def, overrides.TryGetValue(def.Key, out var ov) ? ov : null))
            .ToList();
}

/// <summary>A stored override for an editable setting (#148): the persisted value + provenance.</summary>
public sealed record SettingOverride(string Key, string Value, SettingSource Source, DateTime UpdatedAt, string? UpdatedBy);
