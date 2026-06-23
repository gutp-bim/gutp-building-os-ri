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

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Build_Throws_OnNonPositiveDays(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LakeRetentionLifecycle.Build(days));
    }
}
