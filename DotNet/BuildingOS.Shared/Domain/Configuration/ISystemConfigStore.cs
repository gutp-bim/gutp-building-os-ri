namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>
/// Persistence for editable-setting overrides (#148). Only the value/provenance is stored; the
/// allowlist, validation and defaults live in <see cref="SettingsRegistry"/> / <see cref="SettingsLogic"/>.
/// </summary>
public interface ISystemConfigStore
{
    Task<IReadOnlyList<SettingOverride>> GetAllAsync(CancellationToken ct = default);

    Task UpsertAsync(string key, string value, SettingSource source, string? updatedBy, CancellationToken ct = default);

    /// <summary>Removes an override (reset to default). Returns whether a row existed.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
}
