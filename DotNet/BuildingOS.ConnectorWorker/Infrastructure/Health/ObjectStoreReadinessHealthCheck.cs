using Amazon.S3;
using Amazon.S3.Model;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildingOS.ConnectorWorker.Infrastructure.Health;

/// <summary>
/// Readiness check (#399): the lake's object store (MinIO/S3) answers. Registered only for the roles
/// that persist telemetry — <see cref="Startup.WorkerRoles.RunsLake"/> — and only when the lake path
/// actually put an <see cref="IAmazonS3"/> in DI (parquet mode, or the timescale cold exporter).
/// <para>
/// The probe is a one-key <c>ListObjectsV2</c> on the lake bucket, not <c>IBlobStorage.ListAsync</c>:
/// that one pages through every key under the prefix, so on a large lake a readiness probe would cost
/// a full listing every ten seconds.
/// </para>
/// <para>
/// <b>A missing bucket is Healthy.</b> The store answered — the lake is simply empty, which is the
/// normal state before the writer's first flush. This is the same judgement
/// <c>MinioBlobStorage.ListAsync</c> makes, and it is caught on the SDK's modeled
/// <see cref="NoSuchBucketException"/> so an unrelated S3 failure is not swallowed with it.
/// </para>
/// <para>
/// Severity comes from the registration's <see cref="HealthCheckRegistration.FailureStatus"/> — see
/// <see cref="Startup.ConnectorWorkerHealthChecks"/> for why it gates readiness on <c>lake</c> but only
/// signals on <c>all</c>.
/// </para>
/// </summary>
public sealed class ObjectStoreReadinessHealthCheck(IAmazonS3 s3) : IHealthCheck
{
    /// <summary>The bucket the lake writer writes to — the one whose reachability readiness is about.</summary>
    public const string Bucket = MinioParquetLakeWriter.LakeBucket;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var request = new ListObjectsV2Request { BucketName = Bucket, MaxKeys = 1 };
        try
        {
            await s3.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"Object store answered for bucket '{Bucket}'");
        }
        catch (NoSuchBucketException)
        {
            return HealthCheckResult.Healthy($"Object store answered; bucket '{Bucket}' does not exist yet");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // As in the twin check, a cancelled probe is the framework's to report (it applies this
            // registration's FailureStatus to a timeout).
            return new HealthCheckResult(
                context.Registration.FailureStatus, $"Object store did not answer for bucket '{Bucket}'", ex);
        }
    }
}
