using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildingOS.ConnectorWorker.Infrastructure.Health;

/// <summary>
/// Readiness check (#399): the digital twin (OxiGraph) answers queries. Registered for every role that
/// registers the read-only twin client — <see cref="Startup.WorkerRoles.RunsTwinClient"/>, i.e. all /
/// ingest / control, whose protocol connectors, gRPC ingress metadata cache and Hono control handler
/// all resolve <see cref="OxiGraphClient"/>.
/// <para>
/// <b>A twin failure is reported as Degraded, never as Unhealthy — deliberately.</b> The roles that read
/// the twin do so through TTL caches that keep serving across an outage:
/// <c>PointMetadataCache</c> answers stale-while-revalidate once warm (#371) and <c>PointIdFactory</c>
/// caches its mappings for five minutes, so ingest keeps enriching and publishing frames while OxiGraph
/// is down. Failing readiness would take every ingest replica out of its Service *simultaneously* (they
/// share the one twin) and turn a degradation the caches already absorb into a full ingest outage —
/// strictly worse than the pre-#399 NATS-only readiness. On <c>all</c> it would additionally break the
/// default OSS experience, whose bring-up gate is a plain <c>curl -sf .../health/ready</c>
/// (<c>make wait-oss-stack</c>). Degraded still renders in the <c>/health/ready</c> body and in the
/// startup log line, so an operator sees it; it just does not evict a working replica.
/// </para>
/// <para>
/// The severity is not hard-coded here: it is taken from the registration's
/// <see cref="HealthCheckRegistration.FailureStatus"/>, so <see cref="Startup.ConnectorWorkerHealthChecks"/>
/// stays the single place the per-role policy is written down.
/// </para>
/// </summary>
public sealed class TwinReadinessHealthCheck(OxiGraphClient client) : IHealthCheck
{
    /// <summary>
    /// Cheapest possible round trip that proves the store is answering queries. Deliberately a local
    /// constant rather than a reference to <c>OxiGraphSeedHostedService</c>'s identical startup probe:
    /// that one is internal to BuildingOS.Shared (its test fakes route on exact query text) and the two
    /// probes are free to diverge — widening Shared's public API to share a 36-character string would
    /// couple them for no benefit.
    /// </summary>
    public const string ProbeQuery = "SELECT ?s WHERE { ?s ?p ?o } LIMIT 1";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.QueryAsync(ProbeQuery, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("OxiGraph answered the readiness query");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // OperationCanceledException is left to the framework: it means either the probe budget
            // (HealthCheckRegistration.Timeout) elapsed or the request was aborted, and HealthCheckService
            // already reports the former with this registration's FailureStatus.
            return new HealthCheckResult(
                context.Registration.FailureStatus, "OxiGraph did not answer the readiness query", ex);
        }
    }
}
