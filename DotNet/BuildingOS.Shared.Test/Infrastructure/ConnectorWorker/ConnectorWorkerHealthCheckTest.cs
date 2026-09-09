using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Amazon.S3;
using Amazon.S3.Model;
using BuildingOS.ConnectorWorker.Infrastructure.Health;
using BuildingOS.ConnectorWorker.Startup;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Test.Infrastructure.OxiGraph;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using NATS.Client.Core;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

/// <summary>
/// #399: what <c>/health/ready</c> MEANS per <c>WORKER_ROLE</c>. Readiness used to be "the NATS
/// connection is Open" for every role, so a lake replica whose MinIO was unreachable and an ingest
/// replica whose OxiGraph was unreachable both reported ready. These assert the two halves of the fix:
/// which dependency checks a role registers, and how severely a failure of each is reported.
/// <para>
/// Like the sibling <see cref="ConnectorWorkerRoleTest"/> these never resolve the dependency clients —
/// no NATS/MinIO/OxiGraph is contacted. The provider is built only to read back the registered
/// <see cref="HealthCheckRegistration"/>s (which live in options), and the guard against "registered a
/// check whose dependency is not in DI" — the misconfiguration that turns /health/ready into a 500 —
/// asserts the dependency's service type is present in the collection rather than resolving it.
/// </para>
/// </summary>
public class ConnectorWorkerHealthCheckTest
{
    // DisableDefaults so the machine's ambient environment is NOT loaded — these tests pin gate
    // conditions that depend on keys being absent (WARM_STORE / MINIO_ENDPOINT).
    private static HostApplicationBuilder NewBuilder(Dictionary<string, string?>? env = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(env ?? []);
        return builder;
    }

    // The same widest-possible configuration the role test uses, so every optional gate is exercised.
    private static Dictionary<string, string?> FullyGatedEnv() => new()
    {
        ["MINIO_ENDPOINT"] = "http://localhost:9000",
        ["ENABLE_SIM_CONTROL"] = "true",
        ["MQTT_HOST"] = "mosquitto",
        ["HONO_AMQP_HOST"] = "hono.example",
    };

    /// <summary>The Program.cs shape: the capability graph for a role, then its health registrations.</summary>
    private static HostApplicationBuilder BuildRole(WorkerRole role, Dictionary<string, string?>? env = null)
    {
        var builder = NewBuilder(env ?? FullyGatedEnv());
        builder.AddConnectorWorkerCapabilities(role, grpcIngressPort: 5051);
        builder.AddConnectorWorkerHealthChecks(role);
        return builder;
    }

    private static IReadOnlyList<HealthCheckRegistration> ReadinessChecks(IServiceCollection services)
        => services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations
            .Where(r => r.Tags.Contains(ConnectorWorkerHealthChecks.ReadyTag))
            .ToList();

    private static string[] CheckNames(WorkerRole role, Dictionary<string, string?>? env = null)
        => ReadinessChecks(BuildRole(role, env).Services)
            .Select(r => r.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    // ── which checks each role registers ─────────────────────────────────────

    [Theory]
    [InlineData(WorkerRole.All)]
    [InlineData(WorkerRole.Ingest)]
    [InlineData(WorkerRole.Lake)]
    [InlineData(WorkerRole.Control)]
    public void EveryRole_RegistersTheNatsCheck(WorkerRole role)
    {
        // Every role consumes or publishes on NATS, so this one is unconditional — the pre-#399
        // meaning of readiness, preserved.
        Assert.Contains(ConnectorWorkerHealthChecks.NatsCheckName, CheckNames(role));
    }

    [Fact]
    public void All_RegistersNatsTwinAndObjectStore()
    {
        Assert.Equal(
            [ConnectorWorkerHealthChecks.NatsCheckName,
             ConnectorWorkerHealthChecks.ObjectStoreCheckName,
             ConnectorWorkerHealthChecks.TwinCheckName],
            CheckNames(WorkerRole.All));
    }

    [Fact]
    public void Ingest_RegistersTwin_ButNotTheObjectStoreItNeverWritesTo()
    {
        Assert.Equal(
            [ConnectorWorkerHealthChecks.NatsCheckName, ConnectorWorkerHealthChecks.TwinCheckName],
            CheckNames(WorkerRole.Ingest));
    }

    [Fact]
    public void Lake_RegistersObjectStore_ButNotTheTwinItHasNoClientFor()
    {
        // Lake is the one role with RunsTwinClient() == false, so OxiGraphClient is not in DI. A twin
        // check registered here would throw on resolution and turn /health/ready into a 500.
        Assert.Equal(
            [ConnectorWorkerHealthChecks.NatsCheckName, ConnectorWorkerHealthChecks.ObjectStoreCheckName],
            CheckNames(WorkerRole.Lake));
    }

    [Fact]
    public void Control_RegistersTwin_BecauseTheHonoHandlerResolvesIt()
    {
        Assert.Equal(
            [ConnectorWorkerHealthChecks.NatsCheckName, ConnectorWorkerHealthChecks.TwinCheckName],
            CheckNames(WorkerRole.Control));
    }

    [Fact]
    public void Lake_TimescaleModeWithoutColdExportConfig_RegistersNoObjectStore()
    {
        // WARM_STORE=timescale with no TIMESCALE_CONNECTION_STRING registers neither the parquet lake
        // writer nor the cold exporter, so nothing put IAmazonS3 in DI and nothing may probe it.
        var env = new Dictionary<string, string?>
        {
            ["WARM_STORE"] = "timescale",
            ["MINIO_ENDPOINT"] = "http://localhost:9000",
        };
        Assert.Equal([ConnectorWorkerHealthChecks.NatsCheckName], CheckNames(WorkerRole.Lake, env));
    }

    [Fact]
    public void Lake_ParquetModeWithoutMinioEndpoint_FailsFastBeforeAnyHealthRegistration()
    {
        // Not a health-check gate at all: AddParquetLakeWriter already refuses to start a parquet-mode
        // lake worker without MINIO_ENDPOINT, so the health registration is never reached. Pinned here
        // so a future "register the object-store check unconditionally for lake" cannot claim this
        // configuration as its motivating case.
        var builder = NewBuilder(new Dictionary<string, string?> { ["ENABLE_SIM_CONTROL"] = "true" });
        Assert.Throws<InvalidOperationException>(
            () => builder.AddConnectorWorkerCapabilities(WorkerRole.Lake, grpcIngressPort: null));
    }

    // ── no check may outlive its dependency ──────────────────────────────────

    [Theory]
    [InlineData(WorkerRole.All)]
    [InlineData(WorkerRole.Ingest)]
    [InlineData(WorkerRole.Lake)]
    [InlineData(WorkerRole.Control)]
    public void EveryRegisteredCheck_HasItsDependencyInTheServiceCollection(WorkerRole role)
    {
        // A HealthCheckRegistration whose factory resolves a service the role never registered makes
        // /health/ready return 500 instead of a status — the single most likely regression here. The
        // dependency types are asserted rather than resolved so nothing constructs an S3/NATS client.
        var services = BuildRole(role).Services;
        var dependencyOf = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [ConnectorWorkerHealthChecks.NatsCheckName] = typeof(INatsConnection),
            [ConnectorWorkerHealthChecks.TwinCheckName] = typeof(OxiGraphClient),
            [ConnectorWorkerHealthChecks.ObjectStoreCheckName] = typeof(IAmazonS3),
        };

        var checks = ReadinessChecks(services);
        Assert.NotEmpty(checks);
        foreach (var check in checks)
        {
            Assert.True(dependencyOf.TryGetValue(check.Name, out var dependency), $"unknown check '{check.Name}'");
            Assert.Contains(services, d => d.ServiceType == dependency);
        }
    }

    // ── how severely a failure is reported ───────────────────────────────────
    //
    // Characterization tests: added once the registration tests above were green, not to drive the
    // implementation. The per-role severity and the probe budget are properties OF a
    // HealthCheckRegistration (FailureStatus / Timeout), so writing them separately would have meant
    // writing the same two arguments twice. They stay because the policy — especially "twin never
    // gates" — is a deliberate availability decision that a later reader would otherwise be free to
    // "tidy up" into the uniform blast-radius rule the plan originally proposed.

    private static HealthCheckRegistration Check(WorkerRole role, string name)
        => Assert.Single(ReadinessChecks(BuildRole(role).Services), r => r.Name == name);

    [Theory]
    [InlineData(WorkerRole.All)]
    [InlineData(WorkerRole.Ingest)]
    [InlineData(WorkerRole.Lake)]
    [InlineData(WorkerRole.Control)]
    public void NatsFailure_GatesReadinessForEveryRole(WorkerRole role)
    {
        // Unchanged from #145: nothing this worker does survives a NATS outage.
        Assert.Equal(HealthStatus.Unhealthy, Check(role, ConnectorWorkerHealthChecks.NatsCheckName).FailureStatus);
    }

    [Theory]
    [InlineData(WorkerRole.All)]
    [InlineData(WorkerRole.Ingest)]
    [InlineData(WorkerRole.Control)]
    public void TwinFailure_OnlySignals_ItNeverEvictsAReplica(WorkerRole role)
    {
        // The availability property this whole design turns on: ingest keeps enriching frames from the
        // PointMetadataCache while OxiGraph is down, so a 503 here would take every ingest replica out
        // of its Service at once for a degradation the cache already absorbs — worse than pre-#399.
        Assert.Equal(HealthStatus.Degraded, Check(role, ConnectorWorkerHealthChecks.TwinCheckName).FailureStatus);
    }

    [Fact]
    public void ObjectStoreFailure_GatesReadinessOnLake_ButOnlySignalsOnAll()
    {
        // lake: persisting to the lake is the role's only job, and it serves no traffic, so 503 costs
        // nothing and says something true. all: the same process is also ingest+control, and its
        // /health/ready is what `make wait-oss-stack` polls with a plain `curl -sf`.
        Assert.Equal(
            HealthStatus.Unhealthy,
            Check(WorkerRole.Lake, ConnectorWorkerHealthChecks.ObjectStoreCheckName).FailureStatus);
        Assert.Equal(
            HealthStatus.Degraded,
            Check(WorkerRole.All, ConnectorWorkerHealthChecks.ObjectStoreCheckName).FailureStatus);
    }

    [Theory]
    [InlineData(WorkerRole.All)]
    [InlineData(WorkerRole.Ingest)]
    [InlineData(WorkerRole.Lake)]
    [InlineData(WorkerRole.Control)]
    public void EveryDependencyCheck_CarriesABoundedProbeBudget(WorkerRole role)
    {
        // A dependency that accepts the connection and then stalls would otherwise hold the probe for
        // HttpClient's ~100s default — far longer than the probe period, so probes would pile up.
        foreach (var check in ReadinessChecks(BuildRole(role).Services)
                     .Where(r => r.Name != ConnectorWorkerHealthChecks.NatsCheckName))
        {
            Assert.Equal(ConnectorWorkerHealthChecks.ProbeTimeout, check.Timeout);
            Assert.NotEqual(System.Threading.Timeout.InfiniteTimeSpan, check.Timeout);
        }
    }

    // ── what each check reports ──────────────────────────────────────────────

    // A registration mirroring the production one but with a caller-chosen failure status and budget,
    // so the severity plumbing and the timeout can be exercised without a 3-second test.
    private static HealthCheckContext ContextFor(
        string name, HealthStatus failureStatus, TimeSpan? timeout = null)
        => new()
        {
            Registration = new HealthCheckRegistration(
                name, _ => throw new NotSupportedException("not resolved in this test"),
                failureStatus, tags: [ConnectorWorkerHealthChecks.ReadyTag], timeout: timeout),
        };

    [Fact]
    public async Task TwinCheck_WhenOxiGraphAnswers_IsHealthy()
    {
        var client = new OxiGraphClient(
            new HttpClient(new FakeHttpHandler(@"{ ""results"": { ""bindings"": [] } }")),
            "http://oxigraph:7878");
        var result = await new TwinReadinessHealthCheck(client).CheckHealthAsync(
            ContextFor(ConnectorWorkerHealthChecks.TwinCheckName, HealthStatus.Degraded), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public async Task TwinCheck_WhenOxiGraphFails_ReportsTheRegistrationsFailureStatus(HealthStatus failureStatus)
    {
        // The per-role policy lives in the registration, so the check must not hard-code a status of
        // its own — otherwise changing the policy in one place would silently not take effect.
        var client = new OxiGraphClient(
            new HttpClient(new FakeHttpHandler("boom", HttpStatusCode.InternalServerError)),
            "http://oxigraph:7878");
        var result = await new TwinReadinessHealthCheck(client).CheckHealthAsync(
            ContextFor(ConnectorWorkerHealthChecks.TwinCheckName, failureStatus), CancellationToken.None);

        Assert.Equal(failureStatus, result.Status);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task ObjectStoreCheck_WhenTheStoreAnswers_IsHealthy()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response());
        var result = await new ObjectStoreReadinessHealthCheck(s3.Object).CheckHealthAsync(
            ContextFor(ConnectorWorkerHealthChecks.ObjectStoreCheckName, HealthStatus.Unhealthy),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ObjectStoreCheck_WhenTheBucketDoesNotExistYet_IsStillHealthy()
    {
        // The store answered — the lake is simply empty, which is the normal state before the writer's
        // first flush. Same judgement MinioBlobStorage.ListAsync makes.
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NoSuchBucketException("no such bucket"));
        var result = await new ObjectStoreReadinessHealthCheck(s3.Object).CheckHealthAsync(
            ContextFor(ConnectorWorkerHealthChecks.ObjectStoreCheckName, HealthStatus.Unhealthy),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public async Task ObjectStoreCheck_WhenTheStoreFails_ReportsTheRegistrationsFailureStatus(
        HealthStatus failureStatus)
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("connection refused"));
        var result = await new ObjectStoreReadinessHealthCheck(s3.Object).CheckHealthAsync(
            ContextFor(ConnectorWorkerHealthChecks.ObjectStoreCheckName, failureStatus), CancellationToken.None);

        Assert.Equal(failureStatus, result.Status);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task HangingDependency_IsCutOffByItsProbeBudget_NotLeftToTheHttpClientDefault()
    {
        // End-to-end through HealthCheckService, because the budget is enforced by the framework from
        // HealthCheckRegistration.Timeout rather than by the check itself. A short budget stands in for
        // the production ProbeTimeout so the test does not have to wait it out.
        var budget = TimeSpan.FromMilliseconds(200);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new OxiGraphClient(new HttpClient(new HangingHttpHandler()), "http://oxigraph:7878"));
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            ConnectorWorkerHealthChecks.TwinCheckName,
            sp => new TwinReadinessHealthCheck(sp.GetRequiredService<OxiGraphClient>()),
            failureStatus: HealthStatus.Degraded,
            tags: [ConnectorWorkerHealthChecks.ReadyTag],
            timeout: budget));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = await services.BuildServiceProvider()
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(CancellationToken.None);
        sw.Stop();

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"probe took {sw.Elapsed} — the budget was not applied");
    }

    /// <summary>A dependency that accepts the request and never answers, until the probe is cancelled.</summary>
    private sealed class HangingHttpHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }
}
