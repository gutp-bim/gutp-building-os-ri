using Amazon.S3;
using Amazon.S3.Model;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Builds the S3/MinIO lifecycle (ILM) configuration that expires lake objects after
/// <c>LAKE_RETENTION_DAYS</c> (#217) — the parquet-mode replacement for TimescaleDB's
/// <c>drop_chunks</c> retention. The rule covers the whole bucket (empty prefix) so every partition
/// ages out uniformly. Applied via <c>PutLifecycleConfiguration</c> on the cold bucket.
/// </summary>
public static class LakeRetentionLifecycle
{
    public const string RuleId = "building-os-lake-retention";

    public static LifecycleConfiguration Build(int retentionDays)
    {
        // Defensive: a 0/negative expiration would mean "expire immediately" — never build that. The
        // caller treats non-positive as "unlimited" and clears the rule instead.
        if (retentionDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays), retentionDays, "retentionDays must be positive.");
        }

        return new LifecycleConfiguration
        {
            Rules = new List<LifecycleRule>
            {
                new LifecycleRule
                {
                    Id = RuleId,
                    Status = LifecycleRuleStatus.Enabled,
                    Filter = new LifecycleFilter
                    {
                        LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = string.Empty },
                    },
                    Expiration = new LifecycleRuleExpiration { Days = retentionDays },
                },
            },
        };
    }
}
