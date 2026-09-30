using System.Globalization;

namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>
/// The effective telemetry stale-detection thresholds (#183), readable by all roles via
/// <c>GET /api/telemetry/config</c>. Only these two numbers are exposed — no other settings — so a
/// non-admin surface leaks nothing else.
/// </summary>
public sealed record TelemetryThresholds(double StaleThresholdSeconds, double StaleIntervalMultiplier);

/// <summary>
/// The effective warn thresholds for the /platform/status pipeline KPIs (#456). A KPI above its
/// threshold is shown in the warn colour; the values are editable in /platform/settings
/// (<see cref="SettingsRegistry"/> keys <c>platform.kpi.*</c>).
/// </summary>
public sealed record PipelineKpiThresholds(
    double RejectedPercentWarn,
    double EventLagP95WarnSeconds,
    double ConsumerLagP95WarnSeconds,
    double ParquetFreshnessWarnSeconds,
    double NatsPendingWarn)
{
    /// <summary>The registry defaults — also the fallback when the settings store is unreachable.</summary>
    public static readonly PipelineKpiThresholds Defaults = new(
        RejectedPercentWarn: 1,
        EventLagP95WarnSeconds: 30,
        ConsumerLagP95WarnSeconds: 5,
        ParquetFreshnessWarnSeconds: 600,
        NatsPendingWarn: 10000);
}

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

    /// <summary>
    /// The effective telemetry stale-detection thresholds only (#183). Backs the all-role
    /// <c>GET /api/telemetry/config</c> so freshness classification can read admin-set overrides
    /// without exposing the full (admin-only) settings list.
    /// </summary>
    Task<TelemetryThresholds> GetTelemetryThresholdsAsync(CancellationToken ct = default);

    /// <summary>The effective /platform/status pipeline KPI warn thresholds (#456).</summary>
    Task<PipelineKpiThresholds> GetPipelineKpiThresholdsAsync(CancellationToken ct = default);

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

    public async Task<TelemetryThresholds> GetTelemetryThresholdsAsync(CancellationToken ct = default)
    {
        var views = await GetSettingsAsync(ct).ConfigureAwait(false);
        var byKey = views.ToDictionary(v => v.Key, StringComparer.Ordinal);
        return new TelemetryThresholds(
            StaleThresholdSeconds: EffectiveNumber(byKey, SettingsRegistry.StaleThresholdSecondsKey, 300),
            StaleIntervalMultiplier: EffectiveNumber(byKey, SettingsRegistry.StaleIntervalMultiplierKey, 3));
    }

    public async Task<PipelineKpiThresholds> GetPipelineKpiThresholdsAsync(CancellationToken ct = default)
    {
        var views = await GetSettingsAsync(ct).ConfigureAwait(false);
        var byKey = views.ToDictionary(v => v.Key, StringComparer.Ordinal);
        var d = PipelineKpiThresholds.Defaults;
        return new PipelineKpiThresholds(
            RejectedPercentWarn: EffectiveNumber(byKey, SettingsRegistry.RejectedPercentWarnKey, d.RejectedPercentWarn),
            EventLagP95WarnSeconds: EffectiveNumber(byKey, SettingsRegistry.EventLagP95WarnSecondsKey, d.EventLagP95WarnSeconds),
            ConsumerLagP95WarnSeconds: EffectiveNumber(byKey, SettingsRegistry.ConsumerLagP95WarnSecondsKey, d.ConsumerLagP95WarnSeconds),
            ParquetFreshnessWarnSeconds: EffectiveNumber(byKey, SettingsRegistry.ParquetFreshnessWarnSecondsKey, d.ParquetFreshnessWarnSeconds),
            NatsPendingWarn: EffectiveNumber(byKey, SettingsRegistry.NatsPendingWarnKey, d.NatsPendingWarn));
    }

    // The registry guarantees these keys exist and validates Number values on write, but parse
    // defensively so a hand-edited DB row can never make freshness classification throw.
    private static double EffectiveNumber(
        IReadOnlyDictionary<string, SettingView> byKey, string key, double fallback) =>
        byKey.TryGetValue(key, out var view)
        && double.TryParse(view.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
        && double.IsFinite(n) // "NaN"/"Infinity" parse as numbers but cannot be serialized to JSON
            ? n
            : fallback;

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
