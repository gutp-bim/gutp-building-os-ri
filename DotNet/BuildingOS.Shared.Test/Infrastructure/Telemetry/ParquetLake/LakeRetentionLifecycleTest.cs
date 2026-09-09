using Amazon.S3;
using Amazon.S3.Model;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class LakeRetentionLifecycleTest
{
    [Fact]
    public void Build_SetsEnabledExpirationRuleOverWholeBucket()
    {
        var cfg = LakeRetentionLifecycle.Build(120);

        var rule = Assert.Single(cfg.Rules);
        Assert.Equal(LakeRetentionLifecycle.RuleId, rule.Id);
        Assert.Equal(LifecycleRuleStatus.Enabled, rule.Status);
        Assert.Equal(120, rule.Expiration.Days);
        Assert.Equal(string.Empty, rule.Filter.LifecycleFilterPredicate is LifecyclePrefixPredicate p ? p.Prefix : "?");
    }

    [Fact]
    public void Build_IsAPureFunctionOfTheDays_SoConcurrentAppliesConverge()
    {
        // Characterization (#447): two lake replicas each apply this once at startup, and
        // PutLifecycleConfiguration replaces the bucket's whole rule set rather than adding to it. That
        // makes the second apply a no-op only while the configuration is derived from retentionDays
        // alone under a fixed rule id — give the rule a per-process identity (a hostname, a timestamp,
        // a GUID) and two replicas would install two overlapping rules instead of one.
        static string Describe(LifecycleConfiguration c) => string.Join(";", c.Rules.Select(r =>
            $"{r.Id}|{r.Status}|{r.Expiration.Days}|" +
            $"{(r.Filter.LifecycleFilterPredicate is LifecyclePrefixPredicate p ? p.Prefix : "?")}"));

        Assert.Equal(Describe(LakeRetentionLifecycle.Build(30)), Describe(LakeRetentionLifecycle.Build(30)));
        Assert.Single(LakeRetentionLifecycle.Build(30).Rules);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Build_Throws_OnNonPositiveDays(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LakeRetentionLifecycle.Build(days));
    }
}
