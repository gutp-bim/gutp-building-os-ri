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
