using BuildingOS.Shared.Domain.Configuration;

namespace BuildingOS.Shared.Test.Domain.Configuration;

public class SettingsLogicTest
{
    private static SettingDefinition Bool() =>
        new("ui.flag", SettingType.Boolean, "false", "flag", "ui");

    private static SettingDefinition Num() =>
        new("t.threshold", SettingType.Number, "300", "threshold", "telemetry");

    private static SettingDefinition Str() =>
        new("x.label", SettingType.String, "hello", "label", "misc");

    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("TRUE", "true")]
    public void Validate_Boolean_NormalizesAcceptedValues(string input, string normalized)
    {
        var r = SettingsLogic.Validate(Bool(), input);
        Assert.True(r.IsValid);
        Assert.Equal(normalized, r.Normalized);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public void Validate_Boolean_RejectsNonBoolean(string input)
        => Assert.False(SettingsLogic.Validate(Bool(), input).IsValid);

    [Theory]
    [InlineData("300", "300")]
    [InlineData("12.5", "12.5")]
    [InlineData("-1", "-1")]
    public void Validate_Number_AcceptsAndNormalizes(string input, string normalized)
    {
        var r = SettingsLogic.Validate(Num(), input);
        Assert.True(r.IsValid);
        Assert.Equal(normalized, r.Normalized);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public void Validate_Number_RejectsNonNumber(string input)
        => Assert.False(SettingsLogic.Validate(Num(), input).IsValid);

    [Fact]
    public void Validate_String_AcceptsAnything()
    {
        var r = SettingsLogic.Validate(Str(), "any value");
        Assert.True(r.IsValid);
        Assert.Equal("any value", r.Normalized);
    }

    [Fact]
    public void Merge_NoOverride_UsesDefault_AndSourceDefault()
    {
        var view = SettingsLogic.Merge(Num(), null);
        Assert.Equal("300", view.Value);
        Assert.False(view.IsOverridden);
        Assert.Equal(SettingSource.Default, view.Source);
        Assert.Null(view.UpdatedAt);
    }

    [Fact]
    public void Merge_WithOverride_UsesOverrideValueAndProvenance()
    {
        var at = new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);
        var ov = new SettingOverride("t.threshold", "600", SettingSource.Ui, at, "admin@x");
        var view = SettingsLogic.Merge(Num(), ov);
        Assert.Equal("600", view.Value);
        Assert.True(view.IsOverridden);
        Assert.Equal(SettingSource.Ui, view.Source);
        Assert.Equal(at, view.UpdatedAt);
        Assert.Equal("admin@x", view.UpdatedBy);
        Assert.Equal("300", view.DefaultValue);
    }

    [Fact]
    public void BuildViews_IgnoresStaleOverridesNotInRegistry()
    {
        var defs = new[] { Bool(), Num() };
        var overrides = new Dictionary<string, SettingOverride>
        {
            ["t.threshold"] = new("t.threshold", "600", SettingSource.Ui, DateTime.UtcNow, null),
            ["removed.key"] = new("removed.key", "x", SettingSource.Ui, DateTime.UtcNow, null),
        };

        var views = SettingsLogic.BuildViews(defs, overrides);

        Assert.Equal(2, views.Count);
        Assert.DoesNotContain(views, v => v.Key == "removed.key");
        Assert.True(views.Single(v => v.Key == "t.threshold").IsOverridden);
        Assert.False(views.Single(v => v.Key == "ui.flag").IsOverridden);
    }

    [Fact]
    public void Registry_FindsSeededKeys_AndNullForUnknown()
    {
        Assert.NotNull(SettingsRegistry.Find("ui.showExperimentalFeatures"));
        Assert.NotNull(SettingsRegistry.Find("telemetry.staleThresholdSeconds"));
        // telemetry.staleIntervalMultiplier is intentionally NOT registered as an editable setting in
        // this slice (fixed default 3) — it would be a false affordance until read at runtime (#183).
        Assert.Null(SettingsRegistry.Find("telemetry.staleIntervalMultiplier"));
        Assert.Null(SettingsRegistry.Find("nope"));
    }
}
