namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>
/// Allowlist of editable app settings (#148). Only feature-flag / threshold style application or
/// domain settings that do not conflict with GitOps live here; infra/secrets stay read-only (#147).
/// Extend by adding definitions — the store, validation and edit UI are all driven by this registry.
/// </summary>
public static class SettingsRegistry
{
    public static readonly IReadOnlyList<SettingDefinition> Definitions = new[]
    {
        new SettingDefinition(
            Key: "ui.showExperimentalFeatures",
            Type: SettingType.Boolean,
            DefaultValue: "false",
            Description: "実験的な UI 機能を表示する（フィーチャーフラグ）",
            Category: "ui"),
        new SettingDefinition(
            Key: "telemetry.staleThresholdSeconds",
            Type: SettingType.Number,
            DefaultValue: "300",
            Description: "テレメトリを「鮮度切れ」とみなすまでの秒数（期待周期が未設定のポイントの既定閾値, #183）",
            Category: "telemetry"),
        // The per-point expected-interval multiplier N (threshold = interval × N, #183). Now read at
        // runtime by all roles via GET /api/telemetry/config (TelemetryConfigController), so editing it
        // here actually changes stale classification on home + point detail (no longer a false
        // affordance — the #210 review follow-up).
        new SettingDefinition(
            Key: "telemetry.staleIntervalMultiplier",
            Type: SettingType.Number,
            DefaultValue: "3",
            Description: "期待周期から鮮度切れ閾値を導く倍率 N（閾値 = 期待周期 × N, #183）",
            Category: "telemetry"),
    };

    /// <summary>The telemetry stale-threshold setting keys, exposed all-role via /api/telemetry/config.</summary>
    public const string StaleThresholdSecondsKey = "telemetry.staleThresholdSeconds";
    public const string StaleIntervalMultiplierKey = "telemetry.staleIntervalMultiplier";

    /// <summary>Returns the definition for <paramref name="key"/>, or null when it is not allowlisted.</summary>
    public static SettingDefinition? Find(string key) =>
        Definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
}
