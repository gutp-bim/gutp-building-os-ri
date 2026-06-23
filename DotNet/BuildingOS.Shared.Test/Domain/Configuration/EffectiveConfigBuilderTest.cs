using BuildingOS.Shared.Domain.Configuration;

namespace BuildingOS.Shared.Test.Domain.Configuration;

public class EffectiveConfigBuilderTest
{
    [Fact]
    public void ToEntry_NonSecret_CarriesValueWhenSet()
    {
        var entry = EffectiveConfigBuilder.ToEntry("NATS_URL", isSecret: false, "nats://localhost:4222");
        Assert.False(entry.IsSecret);
        Assert.True(entry.IsSet);
        Assert.Equal("nats://localhost:4222", entry.Value);
    }

    [Fact]
    public void ToEntry_NonSecret_NotSet_HasNullValue()
    {
        var entry = EffectiveConfigBuilder.ToEntry("PROMETHEUS_URL", isSecret: false, null);
        Assert.False(entry.IsSet);
        Assert.Null(entry.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ToEntry_TreatsNullOrEmptyAsNotSet(string? raw)
    {
        var entry = EffectiveConfigBuilder.ToEntry("KEYCLOAK_REALM", isSecret: false, raw);
        Assert.False(entry.IsSet);
    }

    [Fact]
    public void ToEntry_Secret_NeverCarriesValue_EvenWhenSet()
    {
        var entry = EffectiveConfigBuilder.ToEntry("POSTGRES_CONNECTION_STRING", isSecret: true, "Host=db;Password=hunter2");
        Assert.True(entry.IsSecret);
        Assert.True(entry.IsSet); // presence is still reported
        Assert.Null(entry.Value); // but the secret value is dropped
    }

    [Fact]
    public void ToEntry_Secret_NotSet_HasNullValue_AndIsSetFalse()
    {
        var entry = EffectiveConfigBuilder.ToEntry("KEYCLOAK_ADMIN_CLIENT_SECRET", isSecret: true, null);
        Assert.False(entry.IsSet);
        Assert.Null(entry.Value);
    }

    [Fact]
    public void Build_OnlyReadsAllowlistedKeys_AndMasksSecrets()
    {
        var lookup = new Dictionary<string, string?>
        {
            ["NATS_URL"] = "nats://localhost:4222",
            ["POSTGRES_CONNECTION_STRING"] = "Host=db;Password=secret",
            ["UNLISTED_SECRET"] = "should-never-appear",
        };

        var config = EffectiveConfigBuilder.Build(
            new[] { ("NATS_URL", false), ("POSTGRES_CONNECTION_STRING", true) },
            key => lookup.TryGetValue(key, out var v) ? v : null);

        Assert.Equal(2, config.Entries.Count);
        Assert.DoesNotContain(config.Entries, e => e.Key == "UNLISTED_SECRET");

        var nats = config.Entries.Single(e => e.Key == "NATS_URL");
        Assert.Equal("nats://localhost:4222", nats.Value);

        var pg = config.Entries.Single(e => e.Key == "POSTGRES_CONNECTION_STRING");
        Assert.True(pg.IsSet);
        Assert.Null(pg.Value);
    }
}
