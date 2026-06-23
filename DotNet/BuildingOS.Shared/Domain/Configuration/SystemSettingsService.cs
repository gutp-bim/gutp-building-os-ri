namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>Outcome of an update: success (with the merged view), unknown key, or validation failure.</summary>
public enum SettingUpdateStatus
{
    Ok,
    UnknownKey,
    Invalid,
}

public sealed record SettingUpdateResult(SettingUpdateStatus Status, SettingView? View, string? Error)
{
    public static SettingUpdateResult Ok(SettingView view) => new(SettingUpdateStatus.Ok, view, null);
    public static SettingUpdateResult UnknownKey() => new(SettingUpdateStatus.UnknownKey, null, null);
    public static SettingUpdateResult Invalid(string error) => new(SettingUpdateStatus.Invalid, null, error);
}

/// <summary>
/// Combines the editable-setting registry with persisted overrides (#148): lists effective settings,
/// validates + persists updates (only allowlisted keys, type-checked), and resets to default. The
/// registry/validation/merge is pure (<see cref="SettingsLogic"/>); only persistence is async.
/// </summary>
public interface ISystemSettingsService
{
    Task<IReadOnlyList<SettingView>> GetSettingsAsync(CancellationToken ct = default);
    Task<SettingUpdateResult> UpdateSettingAsync(string key, string? value, string? updatedBy, CancellationToken ct = default);

    /// <summary>Resets a setting to its default. Returns false when the key is not allowlisted.</summary>
    Task<bool> ResetSettingAsync(string key, CancellationToken ct = default);
}

public sealed class SystemSettingsService : ISystemSettingsService
{
    private readonly ISystemConfigStore _store;

    public SystemSettingsService(ISystemConfigStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<SettingView>> GetSettingsAsync(CancellationToken ct = default)
    {
        var overrides = await _store.GetAllAsync(ct).ConfigureAwait(false);
        var byKey = overrides.ToDictionary(o => o.Key, StringComparer.Ordinal);
        return SettingsLogic.BuildViews(SettingsRegistry.Definitions, byKey);
    }

    public async Task<SettingUpdateResult> UpdateSettingAsync(
        string key, string? value, string? updatedBy, CancellationToken ct = default)
    {
        var definition = SettingsRegistry.Find(key);
        if (definition is null)
        {
            return SettingUpdateResult.UnknownKey();
        }

        var validation = SettingsLogic.Validate(definition, value);
        if (!validation.IsValid)
        {
            return SettingUpdateResult.Invalid(validation.Error ?? "invalid value");
        }

        var normalized = validation.Normalized!;
        await _store.UpsertAsync(key, normalized, SettingSource.Ui, updatedBy, ct).ConfigureAwait(false);

        var view = SettingsLogic.Merge(
            definition,
            new SettingOverride(key, normalized, SettingSource.Ui, DateTime.UtcNow, updatedBy));
        return SettingUpdateResult.Ok(view);
    }

    public async Task<bool> ResetSettingAsync(string key, CancellationToken ct = default)
    {
        if (SettingsRegistry.Find(key) is null)
        {
            return false;
        }

        await _store.DeleteAsync(key, ct).ConfigureAwait(false);
        return true;
    }
}
