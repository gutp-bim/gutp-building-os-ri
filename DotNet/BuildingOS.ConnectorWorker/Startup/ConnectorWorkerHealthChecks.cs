using Amazon.S3;
using BuildingOS.ConnectorWorker.Infrastructure.Health;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace BuildingOS.ConnectorWorker.Startup;

/// <summary>
/// What <c>/health/ready</c> means for this worker (#399). Before the role switch, readiness was
/// "the NATS connection is Open" for every process — true but incomplete once one image runs as an
/// ingest, lake or control replica: a lake worker whose MinIO is unreachable can persist nothing and
/// still reported ready. Each role now also reports on the dependency its capability set actually
/// needs.
///
/// <para><b>Which checks (registration-driven).</b> The twin and object-store checks are registered
/// exactly when the capability graph registered the client they resolve — <c>OxiGraphClient</c> for
/// <see cref="WorkerRoles.RunsTwinClient"/>, <c>IAmazonS3</c> for <see cref="WorkerRoles.RunsLake"/>
/// in a mode that has an object store. Gating on the registration rather than re-deriving those
/// predicates (plus <c>WARM_STORE</c> and <c>MINIO_ENDPOINT</c>) here is what keeps the two in
/// lockstep: a check whose dependency is absent throws on resolution and turns <c>/health/ready</c>
/// into a 500 — worse than the missing check. It also means this must be called AFTER
/// <see cref="ConnectorWorkerServiceCollectionExtensions.AddConnectorWorkerCapabilities"/>, as
/// Program.cs does.</para>
///
/// <para><b>How severely (blast radius, with one deliberate exception).</b> A dependency whose loss
/// stops this role's only job gates readiness (Unhealthy → 503); anything the role degrades through
/// only signals (Degraded → 200, so a plain <c>curl -sf</c> still passes):
/// <list type="bullet">
/// <item>NATS — <b>gating for every role</b>. Nothing works without it, and this is unchanged from #145.</item>
/// <item>Object store on <c>lake</c> — <b>gating</b>. Writing and compacting the lake is the whole of
/// that role, and a lake replica sits behind no Service, so an unready lake worker costs no traffic:
/// the 503 is a rollout/alert signal, not an eviction.</item>
/// <item>Object store on <c>all</c> — <b>signal only</b>. The same process is also the ingest and
/// control path (neither needs MinIO), and it is what <c>make wait-oss-stack</c> polls.</item>
/// <item>Twin — <b>signal only, on every role</b>. See
/// <see cref="TwinReadinessHealthCheck"/>: the twin is read through TTL caches that keep serving across
/// an outage, and failing readiness would evict every ingest replica at once for a degradation those
/// caches already absorb.</item>
/// </list>
/// Because <c>all</c> never gates on twin or object store, the default OSS bring-up behaves exactly as
/// it did before this change.</para>
/// </summary>
public static class ConnectorWorkerHealthChecks
{
    /// <summary>Tag selecting the checks <c>/health/ready</c> and the overall <c>/health</c> run.</summary>
    public const string ReadyTag = "ready";

    public const string NatsCheckName = "nats";
    public const string TwinCheckName = "twin";
    public const string ObjectStoreCheckName = "objectstore";

    /// <summary>
    /// Per-probe budget. A dependency that accepts the connection and then stalls answers nothing, and
    /// an unbounded probe would sit for HttpClient's ~100s default — far longer than the probe period,
    /// so probes would pile up. Applied as <see cref="HealthCheckRegistration.Timeout"/>, which reports
    /// the elapsed budget with the registration's own failure status.
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Registers the readiness checks this role's dependencies warrant. Liveness stays check-free
    /// (a dependency outage must not trigger a restart loop that cannot fix it, #145).
    /// </summary>
    public static IHostApplicationBuilder AddConnectorWorkerHealthChecks(
        this IHostApplicationBuilder builder, WorkerRole role)
    {
        var checks = builder.Services.AddHealthChecks();

        // Every role consumes or publishes on NATS, and messaging is registered unconditionally.
        checks.AddCheck<NatsReadinessHealthCheck>(NatsCheckName, tags: [ReadyTag]);

        if (IsRegistered<OxiGraphClient>(builder))
            checks.Add(new HealthCheckRegistration(
                TwinCheckName,
                sp => new TwinReadinessHealthCheck(sp.GetRequiredService<OxiGraphClient>()),
                failureStatus: HealthStatus.Degraded,
                tags: [ReadyTag],
                timeout: ProbeTimeout));

        if (IsRegistered<IAmazonS3>(builder))
            checks.Add(new HealthCheckRegistration(
                ObjectStoreCheckName,
                sp => new ObjectStoreReadinessHealthCheck(sp.GetRequiredService<IAmazonS3>()),
                failureStatus: role is WorkerRole.Lake ? HealthStatus.Unhealthy : HealthStatus.Degraded,
                tags: [ReadyTag],
                timeout: ProbeTimeout));

        return builder;
    }

    /// <summary>
    /// The readiness composition as one log-friendly string, e.g. <c>nats(gating), twin(signal)</c>, so
    /// the "listener is up but nothing is actually checked" misconfiguration is visible in the startup
    /// log rather than only by probing.
    /// </summary>
    public static string DescribeReadiness(IEnumerable<HealthCheckRegistration> registrations)
        => string.Join(", ", registrations
            .Where(r => r.Tags.Contains(ReadyTag))
            .Select(r => $"{r.Name}({(r.FailureStatus == HealthStatus.Unhealthy ? "gating" : "signal")})"));

    private static bool IsRegistered<T>(IHostApplicationBuilder builder)
        => builder.Services.Any(d => d.ServiceType == typeof(T));
}
