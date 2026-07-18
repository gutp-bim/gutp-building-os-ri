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
        // NOTE: the per-point expected-interval multiplier N (threshold = interval × N, #183) is a
        // fixed default (3, DEFAULT_STALE_INTERVAL_MULTIPLIER on the frontend) in this slice — it is
        // intentionally NOT exposed as an editable setting here. Editing it would be a false
        // affordance until the freshness classifier reads it at runtime, which needs an all-role
        // (non-admin) telemetry-threshold read surface (GET /api/system/settings is admin-only).
        // Add it back alongside that surface so admin edits actually change stale classification.
    };

    /// <summary>Returns the definition for <paramref name="key"/>, or null when it is not allowlisted.</summary>
    public static SettingDefinition? Find(string key) =>
        Definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
}
