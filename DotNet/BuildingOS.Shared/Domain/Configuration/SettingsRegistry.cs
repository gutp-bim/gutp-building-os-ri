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

        // Pipeline KPI warn thresholds for /platform/status (#456). Read by GET /api/v1/system/status
        // (SystemController attaches the effective values) so the UI colours each KPI against the
        // operator-tuned threshold rather than a hard-coded one.
        new SettingDefinition(
            Key: RejectedPercentWarnKey,
            Type: SettingType.Number,
            DefaultValue: "1",
            Description: "取込拒否率（rejected / ingress, %）がこの値を超えたら /platform/status で警告色にする（#456）",
            Category: "platform"),
        new SettingDefinition(
            Key: EventLagP95WarnSecondsKey,
            Type: SettingType.Number,
            DefaultValue: "30",
            Description: "Event lag p95（イベント時刻 → Hot 到着, 秒）がこの値を超えたら警告色にする（#456）",
            Category: "platform"),
        new SettingDefinition(
            Key: ConsumerLagP95WarnSecondsKey,
            Type: SettingType.Number,
            DefaultValue: "5",
            Description: "Consumer lag p95（JetStream consumer の遅れ, 秒）がこの値を超えたら警告色にする（#456）",
            Category: "platform"),
        new SettingDefinition(
            Key: ParquetFreshnessWarnSecondsKey,
            Type: SettingType.Number,
            DefaultValue: "600",
            Description: "Parquet freshness p95（秒）がこの値を超えたら警告色にする。目安は PARQUET_FLUSH_INTERVAL（分）× 2 × 60 — 既定 600 は flush 間隔 5 分の場合（#456）",
            Category: "platform"),
        new SettingDefinition(
            Key: NatsPendingWarnKey,
            Type: SettingType.Number,
            DefaultValue: "10000",
            Description: "NATS JetStream consumer の未処理件数（全 consumer 合計）がこの値を超えたら警告色にする（#456）",
            Category: "platform"),
    };

    /// <summary>The telemetry stale-threshold setting keys, exposed all-role via /api/telemetry/config.</summary>
    public const string StaleThresholdSecondsKey = "telemetry.staleThresholdSeconds";
    public const string StaleIntervalMultiplierKey = "telemetry.staleIntervalMultiplier";

    /// <summary>Pipeline KPI warn-threshold keys for /platform/status (#456).</summary>
    public const string RejectedPercentWarnKey = "platform.kpi.rejectedPercentWarn";
    public const string EventLagP95WarnSecondsKey = "platform.kpi.eventLagP95WarnSeconds";
    public const string ConsumerLagP95WarnSecondsKey = "platform.kpi.consumerLagP95WarnSeconds";
    public const string ParquetFreshnessWarnSecondsKey = "platform.kpi.parquetFreshnessWarnSeconds";
    public const string NatsPendingWarnKey = "platform.kpi.natsPendingWarn";

    /// <summary>Returns the definition for <paramref name="key"/>, or null when it is not allowlisted.</summary>
    public static SettingDefinition? Find(string key) =>
        Definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
}
