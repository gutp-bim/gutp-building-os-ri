using BuildingOS.Shared.Domain.Configuration;

namespace BuildingOS.Shared.Test.Domain.Configuration;

public class SystemSettingsServiceTest
{
    private const string BoolKey = "ui.showExperimentalFeatures";
    private const string NumKey = "telemetry.staleThresholdSeconds";

    [Fact]
    public async Task GetSettings_MergesOverridesWithDefaults()
    {
        var store = new Mock<ISystemConfigStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SettingOverride(BoolKey, "true", SettingSource.Ui, DateTime.UtcNow, "admin"),
            });

        var service = new SystemSettingsService(store.Object);
        var views = await service.GetSettingsAsync();

        var flag = views.Single(v => v.Key == BoolKey);
        Assert.True(flag.IsOverridden);
        Assert.Equal("true", flag.Value);

        var threshold = views.Single(v => v.Key == NumKey);
        Assert.False(threshold.IsOverridden);
        Assert.Equal("300", threshold.Value); // default
    }

    [Fact]
    public async Task GetTelemetryThresholds_NoOverrides_ReturnsRegistryDefaults()
    {
        var store = new Mock<ISystemConfigStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SettingOverride>());

        var thresholds = await new SystemSettingsService(store.Object).GetTelemetryThresholdsAsync();

        Assert.Equal(300, thresholds.StaleThresholdSeconds);
        Assert.Equal(3, thresholds.StaleIntervalMultiplier);
    }

    [Fact]
    public async Task GetTelemetryThresholds_AppliesAdminOverride_ToMultiplier()
    {
        var store = new Mock<ISystemConfigStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SettingOverride(
                    SettingsRegistry.StaleIntervalMultiplierKey, "5", SettingSource.Ui, DateTime.UtcNow, "admin"),
            });

        var thresholds = await new SystemSettingsService(store.Object).GetTelemetryThresholdsAsync();

        // The admin override (5) is what the all-role read surface serves — so home/point-detail
        // freshness actually reflects the setting (the #210 review follow-up).
        Assert.Equal(5, thresholds.StaleIntervalMultiplier);
        Assert.Equal(300, thresholds.StaleThresholdSeconds); // unchanged default
    }

    [Fact]
    public async Task GetPipelineKpiThresholds_NoOverrides_ReturnsRegistryDefaults()
    {
        var store = new Mock<ISystemConfigStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SettingOverride>());

        var t = await new SystemSettingsService(store.Object).GetPipelineKpiThresholdsAsync();

        Assert.Equal(PipelineKpiThresholds.Defaults, t);
        Assert.Equal(1, t.RejectedPercentWarn);
        Assert.Equal(30, t.EventLagP95WarnSeconds);
        Assert.Equal(5, t.ConsumerLagP95WarnSeconds);
        // PARQUET_FLUSH_INTERVAL default (5 min) × 2.
        Assert.Equal(600, t.ParquetFreshnessWarnSeconds);
        // ≈ 160 s of backlog at the E10 reference ingest rate (1,865 points / 300 s ≈ 6.2 msg/s);
        // healthy num_pending sits at ~0 (73h soak max 2). Rationale: observability-baseline.md.
        Assert.Equal(1000, t.NatsPendingWarn);
    }

    [Fact]
    public void PipelineKpiThresholdDefaults_MatchRegistryDefaults()
    {
        // The record's Defaults is the fallback when the store is unreachable — it must not drift
        // from the registry default that /platform/settings shows and resets to.
        static double Registry(string key) =>
            double.Parse(SettingsRegistry.Find(key)!.DefaultValue, System.Globalization.CultureInfo.InvariantCulture);

        var d = PipelineKpiThresholds.Defaults;
        Assert.Equal(Registry(SettingsRegistry.RejectedPercentWarnKey), d.RejectedPercentWarn);
        Assert.Equal(Registry(SettingsRegistry.EventLagP95WarnSecondsKey), d.EventLagP95WarnSeconds);
        Assert.Equal(Registry(SettingsRegistry.ConsumerLagP95WarnSecondsKey), d.ConsumerLagP95WarnSeconds);
        Assert.Equal(Registry(SettingsRegistry.ParquetFreshnessWarnSecondsKey), d.ParquetFreshnessWarnSeconds);
        Assert.Equal(Registry(SettingsRegistry.NatsPendingWarnKey), d.NatsPendingWarn);
    }

    [Fact]
    public async Task GetPipelineKpiThresholds_AppliesAdminOverride()
    {
        var store = new Mock<ISystemConfigStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SettingOverride(
                    SettingsRegistry.EventLagP95WarnSecondsKey, "90", SettingSource.Ui, DateTime.UtcNow, "admin"),
                new SettingOverride(
                    SettingsRegistry.ParquetFreshnessWarnSecondsKey, "1200", SettingSource.Ui, DateTime.UtcNow, "admin"),
            });

        var t = await new SystemSettingsService(store.Object).GetPipelineKpiThresholdsAsync();

        Assert.Equal(90, t.EventLagP95WarnSeconds);
        Assert.Equal(1200, t.ParquetFreshnessWarnSeconds);
        Assert.Equal(5, t.ConsumerLagP95WarnSeconds); // untouched default
    }

    [Fact]
    public void PipelineKpiThresholdKeys_AreEditableNumberSettings()
    {
        // Editable in /platform/settings and type-validated like the existing keys.
        foreach (var key in new[]
                 {
                     SettingsRegistry.RejectedPercentWarnKey,
                     SettingsRegistry.EventLagP95WarnSecondsKey,
                     SettingsRegistry.ConsumerLagP95WarnSecondsKey,
                     SettingsRegistry.ParquetFreshnessWarnSecondsKey,
                     SettingsRegistry.NatsPendingWarnKey,
                 })
        {
            var def = SettingsRegistry.Find(key);
            Assert.NotNull(def);
            Assert.Equal(SettingType.Number, def!.Type);
            Assert.Equal("platform", def.Category);
            Assert.False(SettingsLogic.Validate(def, "abc").IsValid);
        }
    }

    [Fact]
    public async Task UpdateSetting_UnknownKey_ReturnsUnknown_AndDoesNotPersist()
    {
        var store = new Mock<ISystemConfigStore>();
        var service = new SystemSettingsService(store.Object);

        var result = await service.UpdateSettingAsync("not.a.key", "x", "admin");

        Assert.Equal(SettingUpdateStatus.UnknownKey, result.Status);
        store.Verify(
            s => s.UpsertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SettingSource>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateSetting_InvalidType_ReturnsInvalid_AndDoesNotPersist()
    {
        var store = new Mock<ISystemConfigStore>();
        var service = new SystemSettingsService(store.Object);

        var result = await service.UpdateSettingAsync(NumKey, "not-a-number", "admin");

        Assert.Equal(SettingUpdateStatus.Invalid, result.Status);
        Assert.NotNull(result.Error);
        store.Verify(
            s => s.UpsertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SettingSource>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateSetting_Valid_PersistsNormalizedValue_AndReturnsView()
    {
        var store = new Mock<ISystemConfigStore>();
        var service = new SystemSettingsService(store.Object);

        var result = await service.UpdateSettingAsync(BoolKey, "TRUE", "admin@x");

        Assert.Equal(SettingUpdateStatus.Ok, result.Status);
        Assert.Equal("true", result.View!.Value); // normalized
        Assert.Equal(SettingSource.Ui, result.View.Source);
        store.Verify(
            s => s.UpsertAsync(BoolKey, "true", SettingSource.Ui, "admin@x", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResetSetting_UnknownKey_ReturnsFalse_AndDoesNotDelete()
    {
        var store = new Mock<ISystemConfigStore>();
        var service = new SystemSettingsService(store.Object);

        var ok = await service.ResetSettingAsync("not.a.key");

        Assert.False(ok);
        store.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetSetting_KnownKey_DeletesOverride()
    {
        var store = new Mock<ISystemConfigStore>();
        store.Setup(s => s.DeleteAsync(NumKey, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new SystemSettingsService(store.Object);

        var ok = await service.ResetSettingAsync(NumKey);

        Assert.True(ok);
        store.Verify(s => s.DeleteAsync(NumKey, It.IsAny<CancellationToken>()), Times.Once);
    }
}
