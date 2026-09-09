using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Applies the lake retention ILM rule once at startup (#217) when <c>LAKE_RETENTION_DAYS &gt; 0</c>:
/// objects in the cold bucket expire after the configured number of days (the parquet-mode replacement
/// for TimescaleDB's <c>drop_chunks</c>). A non-positive value means unlimited retention (no rule
/// applied). Failure is logged, not fatal — retention is best-effort and re-applied on the next restart.
///
/// <para><b>Concurrent replicas (#447)</b> need no exclusion here. <c>PutLifecycleConfiguration</c>
/// replaces the bucket's entire rule set (it is a PUT, not an append) and
/// <see cref="LakeRetentionLifecycle.Build"/> derives that set from <c>LAKE_RETENTION_DAYS</c> alone,
/// under a fixed rule id — so N replicas applying it at startup converge on the one rule the first of
/// them wrote, in any order. The one way to break that is to make the rule identity per-process; a
/// characterization test pins it. Two replicas configured with *different* retention days would of
/// course flap, but that is a misconfiguration, not a race.</para>
/// </summary>
public sealed class LakeRetentionHostedService : IHostedService
{
    private readonly IAmazonS3 _s3;
    private readonly int _retentionDays;
    private readonly ILogger<LakeRetentionHostedService> _logger;

    public LakeRetentionHostedService(IAmazonS3 s3, int retentionDays, ILogger<LakeRetentionHostedService> logger)
    {
        _s3 = s3;
        _retentionDays = retentionDays;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bucket = MinioParquetLakeWriter.LakeBucket;
        try
        {
            if (_retentionDays <= 0)
            {
                // Unlimited retention: remove any rule a previous deployment applied, so flipping
                // LAKE_RETENTION_DAYS back to unset/0 actually stops objects from expiring.
                await _s3.DeleteLifecycleConfigurationAsync(
                    new DeleteLifecycleConfigurationRequest { BucketName = bucket }, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation("Lake retention unlimited; cleared ILM on '{Bucket}'", bucket);
                return;
            }

            await _s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
            {
                BucketName = bucket,
                Configuration = LakeRetentionLifecycle.Build(_retentionDays),
            }, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Applied lake retention ILM: objects in '{Bucket}' expire after {Days}d", bucket, _retentionDays);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchLifecycleConfiguration")
        {
            // Nothing to clear — already unlimited.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure lake retention ILM ({Days}d); will retry on next restart", _retentionDays);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
