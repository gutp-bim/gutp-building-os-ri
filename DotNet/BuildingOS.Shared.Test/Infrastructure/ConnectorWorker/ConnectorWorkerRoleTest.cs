using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.ConnectorWorker.Startup;
using BuildingOS.Shared.Infrastructure.ColdExport;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.DeviceControlHandler;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using BuildingOS.Shared.Module;
using Microsoft.Extensions.Configuration;
using NATS.Client.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

/// <summary>
/// #400: WORKER_ROLE selects which capability groups the connector worker registers, so one image can
/// run as the all-in-one worker (the default and the OSS RI shape) or as an ingest / lake / control
/// replica. These assert the *registration* consequence of a role — like the sibling capability tests
/// they never build the provider, so no NATS/MinIO/OxiGraph is contacted.
/// </summary>
public class ConnectorWorkerRoleTest
{
    private static HostApplicationBuilder NewBuilder(Dictionary<string, string?>? env = null)
    {
        // DisableDefaults so the machine's ambient environment variables are NOT loaded — these tests
        // pin gate conditions that depend on keys being absent (WORKER_ROLE / WARM_STORE /
        // MINIO_ENDPOINT / MQTT_HOST / HONO_AMQP_HOST).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(env ?? []);
        return builder;
    }

    // Most hosted services are registered through AddHostedService(sp => new X(...)), which leaves
    // ServiceDescriptor.ImplementationType null. The concrete type survives on the factory delegate:
    // the compiler infers Func<IServiceProvider, X> and the conversion to Func<IServiceProvider, object>
    // is a variance conversion that does not wrap, so GenericTypeArguments[1] is X. This is the same
    // fallback the framework's own ServiceDescriptor.GetImplementationType uses, and it keeps the
    // assertion at "which workers" rather than the weaker "how many".
    private static Type? ImplTypeOf(ServiceDescriptor d)
        => d.ImplementationType
           ?? d.ImplementationInstance?.GetType()
           ?? (d.ImplementationFactory is { } f && f.GetType().GenericTypeArguments.Length == 2
               ? f.GetType().GenericTypeArguments[1]
               : null);

    private static string[] HostedServiceNames(IServiceCollection services)
        => services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => ImplTypeOf(d)?.Name ?? "<unresolved>")
            .ToArray();

    private static bool Registers<T>(IServiceCollection services)
        => services.Any(d => d.ServiceType == typeof(T));

    // Every capability the all-in-one worker had before the role switch, in Program.cs order.
    private static HostApplicationBuilder LegacyAllInOne(Dictionary<string, string?> env, int? grpcIngressPort)
    {
        var b = NewBuilder(env);
        b.AddConnectorWorkerObservability();
        b.AddConnectorWorkerMessaging();
        b.AddConnectorWorkerTwin();
        b.AddConnectorWorkerControl();
        b.AddProtocolConnectors();
        b.AddParquetLakeWriter();
        b.AddColdExportWorker();
        b.AddTelemetryIngress(grpcIngressPort);
        return b;
    }

    // A configuration that exercises every optional gate at once, so the regression guard below
    // compares the widest possible graph rather than the default subset.
    private static Dictionary<string, string?> FullyGatedEnv(string? role = null) => new()
    {
        ["MINIO_ENDPOINT"] = "http://localhost:9000",
        ["ENABLE_SIM_CONTROL"] = "true",
        ["MQTT_HOST"] = "mosquitto",
        ["HONO_AMQP_HOST"] = "hono.example",
        [WorkerRoles.EnvVar] = role,
    };

    // ── The role parser ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("  All  ")]
    public void Parse_UnsetOrAll_IsAll(string? value)
    {
        Assert.Equal(WorkerRole.All, WorkerRoles.Parse(value));
    }

    [Theory]
    [InlineData("ingest", WorkerRole.Ingest)]
    [InlineData("  Ingest ", WorkerRole.Ingest)]
    [InlineData("LAKE", WorkerRole.Lake)]
    [InlineData("control", WorkerRole.Control)]
    public void Parse_KnownRole_IsTrimmedAndCaseInsensitive(string value, WorkerRole expected)
    {
        Assert.Equal(expected, WorkerRoles.Parse(value));
    }

    [Theory]
    [InlineData("lakes")]
    [InlineData("worker")]
    [InlineData("ingest,control")] // a capability *set* is deliberately not accepted (one role per process)
    public void Parse_UnknownRole_ThrowsNamingTheValueAndTheValidSet(string value)
    {
        // Unlike WARM_STORE (unknown → the default), an unrecognised role must not silently collapse to
        // "all": that would run the lake and seed workers on every replica of a split deployment.
        var ex = Assert.Throws<InvalidOperationException>(() => WorkerRoles.Parse(value));
        Assert.Contains(value, ex.Message);
        Assert.Contains("ingest", ex.Message);
        Assert.Contains("lake", ex.Message);
        Assert.Contains("control", ex.Message);
    }

    // ── all: the regression guard ────────────────────────────────────────────

    [Fact]
    public void All_RegistersExactlyWhatTheAllInOneWorkerRegisteredBefore()
    {
        var legacy = LegacyAllInOne(FullyGatedEnv(), 5051);
        var role = NewBuilder(FullyGatedEnv()).AddConnectorWorkerCapabilities(WorkerRole.All, 5051);

        // Hosted-service start order is behaviour, so compare the ordered list, not a set.
        Assert.Equal(HostedServiceNames(legacy.Services), HostedServiceNames(role.Services));

        // Everything else (singletons, options, the gRPC plumbing) must match as a set — splitting the
        // twin capability in two reorders its descriptors without changing what is registered.
        static HashSet<string> Signature(IServiceCollection s)
            => s.Select(d => $"{d.ServiceType.FullName}|{ImplTypeOf(d)?.FullName ?? "factory"}|{d.Lifetime}")
                .ToHashSet();
        Assert.Equal(Signature(legacy.Services), Signature(role.Services));
    }

    [Fact]
    public void All_WithoutIngressPort_MatchesLegacyToo()
    {
        var legacy = LegacyAllInOne(FullyGatedEnv(), null);
        var role = NewBuilder(FullyGatedEnv()).AddConnectorWorkerCapabilities(WorkerRole.All, null);

        Assert.Equal(HostedServiceNames(legacy.Services), HostedServiceNames(role.Services));
        Assert.False(Registers<IIngressTelemetryBus>(role.Services));
    }

    // ── ingest ───────────────────────────────────────────────────────────────

    [Fact]
    public void Ingest_RunsTheTelemetryPathsOnly()
    {
        var b = NewBuilder(FullyGatedEnv("ingest"));
        b.AddConnectorWorkerCapabilities(WorkerRole.Ingest, 5051);
        var hosted = HostedServiceNames(b.Services);

        // The five raw.* normalizers plus both optional ingress workers.
        Assert.Contains(nameof(HvacConnectorWorker), hosted);
        Assert.Contains(nameof(BacnetConnectorWorker), hosted);
        Assert.Contains(nameof(MqttIngressWorker), hosted);
        Assert.Contains(nameof(AmqpIngressWorker), hosted);
        // gRPC GatewayIngress registers no hosted service — its singletons are the tell.
        Assert.True(Registers<IIngressTelemetryBus>(b.Services));
        Assert.True(Registers<IPointMetadataCache>(b.Services));

        // Not the lake, not control, and — the multi-replica safety property — not the twin seed.
        Assert.DoesNotContain(nameof(ParquetLakeWriterWorker), hosted);
        Assert.DoesNotContain(nameof(CompactionWorker), hosted);
        Assert.DoesNotContain(nameof(LakeRetentionHostedService), hosted);
        Assert.DoesNotContain(nameof(NatsPointControlWorker), hosted);
        Assert.DoesNotContain(nameof(OxiGraphSeedHostedService), hosted);
    }

    [Fact]
    public void Ingest_KeepsTheTwinClientItsConnectorsResolve()
    {
        var b = NewBuilder(FullyGatedEnv("ingest"));
        b.AddConnectorWorkerCapabilities(WorkerRole.Ingest, 5051);

        // The protocol connectors take IPointIdFactory/BacnetPointResolver and GatewayIngress takes
        // OxiGraphClient, so dropping the seed must not drop the twin client with it.
        Assert.True(Registers<IPointIdFactory>(b.Services));
        Assert.True(Registers<BacnetPointResolver>(b.Services));
        Assert.True(Registers<OxiGraphClient>(b.Services));
    }

    // ── lake ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Lake_RunsThePersistencePathOnly()
    {
        var b = NewBuilder(FullyGatedEnv("lake"));
        b.AddConnectorWorkerCapabilities(WorkerRole.Lake, null);
        var hosted = HostedServiceNames(b.Services);

        Assert.Equal(
            [nameof(ParquetLakeWriterWorker), nameof(CompactionWorker), nameof(LakeRetentionHostedService)],
            hosted);
        Assert.True(Registers<IParquetLakeWriter>(b.Services));
    }

    [Fact]
    public void Lake_IgnoresTheIngressPortItMayInheritFromASharedConfig()
    {
        // A lake replica that inherits GRPC_INGRESS_PORT from a shared ConfigMap must not open an
        // ingest surface, so the role — not the port — decides.
        var b = NewBuilder(FullyGatedEnv("lake"));
        b.AddConnectorWorkerCapabilities(WorkerRole.Lake, 5051);

        Assert.False(Registers<IIngressTelemetryBus>(b.Services));
        Assert.False(Registers<OxiGraphClient>(b.Services));
    }

    [Fact]
    public void Lake_TimescaleMode_RunsTheColdExportWorkerInstead()
    {
        var b = NewBuilder(new()
        {
            ["WARM_STORE"] = "timescale",
            ["TIMESCALE_CONNECTION_STRING"] = "Host=localhost;Database=bos",
            ["MINIO_ENDPOINT"] = "http://localhost:9000",
        });
        b.AddConnectorWorkerCapabilities(WorkerRole.Lake, null);

        Assert.Equal([nameof(ColdExportWorker)], HostedServiceNames(b.Services));
        Assert.True(Registers<IColdExportService>(b.Services));
    }

    // ── control ──────────────────────────────────────────────────────────────

    [Fact]
    public void Control_RunsThePointControlPathOnly()
    {
        var b = NewBuilder(FullyGatedEnv("control"));
        b.AddConnectorWorkerCapabilities(WorkerRole.Control, 5051);
        var hosted = HostedServiceNames(b.Services);

        Assert.Equal([nameof(NatsPointControlWorker)], hosted);
        Assert.True(Registers<IGatewayConnectionRegistry>(b.Services));
        Assert.False(Registers<IIngressTelemetryBus>(b.Services));
        Assert.False(Registers<IParquetLakeWriter>(b.Services));
    }

    [Fact]
    public void Control_KeepsTheTwinClientTheHonoHandlerResolves()
    {
        // NatsPointControlWorker's factory calls sp.GetServices<IDeviceControlHandler>() eagerly, so a
        // HonoDeviceControlHandler without IPointIdFactory would fail at host start, not at first use.
        var b = NewBuilder(FullyGatedEnv("control"));
        b.AddConnectorWorkerCapabilities(WorkerRole.Control, null);

        Assert.True(Registers<IPointIdFactory>(b.Services));
        Assert.Contains(b.Services, d =>
            d.ServiceType == typeof(IDeviceControlHandler) && ImplTypeOf(d) == typeof(HonoDeviceControlHandler));
    }

    // ── every role ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WorkerRole.All)]
    [InlineData(WorkerRole.Ingest)]
    [InlineData(WorkerRole.Lake)]
    [InlineData(WorkerRole.Control)]
    public void EveryRole_RegistersMessaging_WhichTheReadinessProbeResolves(WorkerRole role)
    {
        var b = NewBuilder(FullyGatedEnv());
        b.AddConnectorWorkerCapabilities(role, null);

        // NatsReadinessHealthCheck takes INatsConnection and is registered for every role in Program.cs.
        Assert.True(Registers<INatsConnection>(b.Services));
        Assert.True(Registers<INatsPublisher>(b.Services));
    }

    [Theory]
    [InlineData(WorkerRole.Ingest)]
    [InlineData(WorkerRole.Lake)]
    [InlineData(WorkerRole.Control)]
    public void OnlyAll_RunsTheTwinSeed(WorkerRole role)
    {
        // OxiGraphSeedHostedService replaces the default graph (DROP DEFAULT), so it must run in exactly
        // one place. Keeping it on `all` preserves today's single-worker behaviour.
        var b = NewBuilder(FullyGatedEnv());
        b.AddConnectorWorkerCapabilities(role, null);

        Assert.DoesNotContain(nameof(OxiGraphSeedHostedService), HostedServiceNames(b.Services));
    }

    // ── the twin split stays backwards compatible ────────────────────────────

    [Fact]
    public void TwinClientPlusTwinSeed_EqualsTheOriginalTwinCapability()
    {
        var whole = NewBuilder();
        whole.AddConnectorWorkerTwin();

        var split = NewBuilder();
        split.AddConnectorWorkerTwinClient();
        split.AddConnectorWorkerTwinSeed();

        static HashSet<string> Types(IServiceCollection s)
            => s.Select(d => $"{d.ServiceType.FullName}|{ImplTypeOf(d)?.FullName ?? "factory"}").ToHashSet();
        Assert.Equal(Types(whole.Services), Types(split.Services));
    }
}
