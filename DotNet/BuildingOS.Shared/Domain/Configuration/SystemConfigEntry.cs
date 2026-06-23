namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>
/// Persisted override for an editable app setting (#148), stored in <c>system_config</c>. Only keys
/// present in <see cref="SettingsRegistry"/> are ever written; the value is the normalized string form.
/// </summary>
public class SystemConfigEntry
{
    public string Key { get; set; } = default!;
    public string Value { get; set; } = default!;
    /// <summary>Provenance of the value (e.g. "Ui"). Stored as a string for forward-compatibility.</summary>
    public string Source { get; set; } = nameof(SettingSource.Ui);
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
