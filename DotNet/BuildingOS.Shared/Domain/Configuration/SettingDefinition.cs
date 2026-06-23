using System.Text.Json.Serialization;

namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>The value type of an editable app setting (#148). Drives validation and the edit UI control.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SettingType
{
    Boolean,
    Number,
    String,
}

/// <summary>
/// Where the effective value of a setting comes from (#148). UI = persisted override edited via the
/// console; Default = the registry default (no override stored).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SettingSource
{
    Default,
    Ui,
}

/// <summary>
/// Definition of one editable app setting (#148). The registry of these is the allowlist — only keys
/// with a definition can be read/edited, so arbitrary or GitOps-owned keys are never writable here.
/// </summary>
public sealed record SettingDefinition(
    string Key,
    SettingType Type,
    string DefaultValue,
    string Description,
    string Category);

/// <summary>
/// One setting's effective view (#148): the merge of its definition and any stored override. Carries
/// the effective <see cref="Value"/>, whether it is overridden, and the override's source/metadata.
/// </summary>
public sealed record SettingView(
    string Key,
    SettingType Type,
    string Description,
    string Category,
    string Value,
    string DefaultValue,
    bool IsOverridden,
    SettingSource Source,
    DateTime? UpdatedAt,
    string? UpdatedBy);
