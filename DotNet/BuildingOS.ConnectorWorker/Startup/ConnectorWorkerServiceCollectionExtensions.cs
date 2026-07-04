using Amazon.Runtime;
using Amazon.S3;
using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.ConnectorWorker.Infrastructure.DeviceControlHandler;
using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.ColdExport;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.DeviceControlHandler;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using BuildingOS.Shared.Module;
using BuildingOS.Shared.Module.Oss;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace BuildingOS.ConnectorWorker.Startup;

/// <summary>
/// Connector-worker service registration, grouped by capability so Program.cs reads as a list of
/// feature registrations rather than one long procedure. Behaviour is identical to the prior inline
/// ConfigureCommon/Register* helpers: the set, order and conditions (gRPC ingress gate, sim/Kandt
/// dispatch, MQTT/Hono env gates, parquet/timescale mutual exclusion) are preserved exactly.
/// </summary>
public static class ConnectorWorkerServiceCollectionExtensions
{
    /// <summary>OpenTelemetry (traces + metrics + logs via OTLP). No-op when the OTLP endpoint is unset.</summary>
    public static IHostApplicationBuilder AddConnectorWorkerObservability(this IHostApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "building-os-connector-worker";
        var sampleRatio = double.TryParse(
            builder.Configuration["OTEL_TRACES_SAMPLER_ARG"],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 1.0;
        builder.Services.AddOtlpTelemetry(otelServiceName, otlpEndpoint, sampleRatio);
        builder.Logging.AddOtlpLogging(otelServiceName, otlpEndpoint);
        return builder;
    }

    /// <summary>NATS connection + JetStream, Hot KV latest store, and the KV-decorating publisher.</summary>
    public static IHostApplicationBuilder AddConnectorWorkerMessaging(this IHostApplicationBuilder builder)
    {
        var natsUrl = builder.Configuration["NATS_URL"] ?? "nats://localhost:4222";
        builder.Services.AddSingleton<INatsConnection>(_ =>
            new NatsConnection(new NatsOpts { Url = natsUrl }));
        builder.Services.AddSingleton<INatsJSContext>(sp =>
            new NatsJSContext(sp.GetRequiredService<INatsConnection>()));

        // Hot telemetry store (NATS KV) — caches latest values per point.
        builder.Services.AddSingleton<IHotTelemetryStore, NatsKvLatestStore>();

        // Publisher: wraps raw NATS publish with KV put for validated telemetry.
        builder.Services.AddSingleton<INatsPublisher>(sp => new NatsKvPublisher(
            new NatsPublisher(sp.GetRequiredService<INatsConnection>()),
            sp.GetRequiredService<IHotTelemetryStore>(),
            sp.GetRequiredService<ILogger<NatsKvPublisher>>()));
        return builder;
    }

    /// <summary>Digital twin (OxiGraph) client + seed import + pointlist-update publisher + PointIdFactory.</summary>
    public static IHostApplicationBuilder AddConnectorWorkerTwin(this IHostApplicationBuilder builder)
    {
        var oxiGraphEndpoint = builder.Configuration["OXIGRAPH_ENDPOINT"] ?? "http://localhost:7878";
        builder.Services.AddHttpClient("oxigraph");
        builder.Services.AddSingleton(sp =>
            new OxiGraphClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("oxigraph"),
                oxiGraphEndpoint));
        // Point-list-update publisher (#224/push): seed import signals each gateway to revalidate.
        builder.Services.AddSingleton<IPointListUpdatePublisher>(sp =>
            new NatsPointListUpdatePublisher(sp.GetRequiredService<INatsConnection>()));
        // Seed OxiGraph with pointlist RDF on startup (no-op when OXIGRAPH_SEED_TTL_PATH is unset).
        builder.Services.AddHostedService<OxiGraphSeedHostedService>();

        // PointIdFactory caches OxiGraph mappings (5-min TTL, retry with backoff on initial load).
        builder.Services.AddSingleton<IPointIdDataSource, OxiGraphPointIdDataSource>();
        builder.Services.AddSingleton<IPointIdFactory>(sp =>
            new PointIdFactory(
                sp.GetRequiredService<IPointIdDataSource>(),
                sp.GetRequiredService<ILogger<PointIdFactory>>()));
        builder.Services.AddSingleton(_ => new BacnetPointResolver());
        return builder;
    }

    /// <summary>
    /// Device control: gateway connection registry, the binding-appropriate handler set
    /// (sim vs Kandt, plus Hono when HONO_AMQP_HOST is set), and the NATS point-control worker.
    /// </summary>
    public static IHostApplicationBuilder AddConnectorWorkerControl(this IHostApplicationBuilder builder)
    {
        // In sim mode only SimulatedDeviceControlHandler is registered; the Kandt handler is skipped
        // because it requires IoT Hub credentials absent in OSS/CI (startup would fail).
        var simControl = string.Equals(
            builder.Configuration["ENABLE_SIM_CONTROL"], "true", StringComparison.OrdinalIgnoreCase);

        // Gateway connection registry (#154 Phase 2): gatewayId → binding + per-gateway settings.
        // In sim mode the only handler is the simulated one, so fall back to "simulated" when no
        // explicit default binding is configured (otherwise an unmapped gateway resolves to "hono").
        builder.Services.AddSingleton<IGatewayConnectionRegistry>(
            _ => GatewayConnectionRegistryFactory.Create(
                builder.Configuration, simControl ? BindingTypes.Simulated : null));
        if (simControl)
            builder.Services.AddSingleton<IDeviceControlHandler, SimulatedDeviceControlHandler>();
        else
            builder.Services.AddSingleton<IDeviceControlHandler, KandtDeviceControlHandler>();

        // Hono control handler gated solely on HONO_AMQP_HOST (Scenario B), independent of sim mode:
        // no credential requirement that aborts startup, and it dispatches on binding="hono".
        if (!string.IsNullOrWhiteSpace(builder.Configuration["HONO_AMQP_HOST"]))
            builder.Services.AddSingleton<IDeviceControlHandler, HonoDeviceControlHandler>();

        builder.Services.AddHostedService(sp => new NatsPointControlWorker(
            Sub("building-os.control.request", "pointcontrolworker", sp),
            sp.GetServices<IDeviceControlHandler>(),
            sp.GetRequiredService<IGatewayConnectionRegistry>(),
            sp.GetRequiredService<INatsPublisher>(),
            sp.GetRequiredService<ILogger<NatsPointControlWorker>>()));
        return builder;
    }

    /// <summary>Per-protocol raw connectors: core (always), MQTT (Scenario A), Hono (Scenario B).</summary>
    public static IHostApplicationBuilder AddProtocolConnectors(this IHostApplicationBuilder builder)
    {
        AddCoreConnectors(builder);
        AddMqttConnectors(builder);
        AddHonoConnectors(builder);
        return builder;
    }

    /// <summary>
    /// gRPC GatewayIngress (#181): point-id telemetry → twin enrich → validated.telemetry. Registered
    /// only when GRPC_INGRESS_PORT is set (passed in, since the listener port is resolved pre-builder).
    /// </summary>
    public static IHostApplicationBuilder AddTelemetryIngress(this IHostApplicationBuilder builder, int? grpcIngressPort)
    {
        if (grpcIngressPort is null) return builder;

        builder.Services.AddGrpc();
        // Identity binding (#296): bind frame gateway_id to the mTLS-ingress trusted header when
        // GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY=true (default off preserves provenance-only).
        builder.Services.AddSingleton(IngressIdentityOptions.Parse(
            builder.Configuration["GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY"],
            builder.Configuration["GRPC_INGRESS_GATEWAY_ID_HEADER"]));
        // Ingress bus publishes via JetStream publish-ack (#187) + keeps the KV hot store in sync.
        builder.Services.AddSingleton<IIngressTelemetryBus>(sp => new NatsIngressTelemetryBus(
            sp.GetRequiredService<INatsJSContext>(),
            sp.GetRequiredService<IHotTelemetryStore>(),
            sp.GetRequiredService<ILogger<NatsIngressTelemetryBus>>()));
        builder.Services.AddSingleton<IPointMetadataDataSource>(sp =>
            new OxiGraphPointMetadataDataSource(sp.GetRequiredService<OxiGraphClient>()));
        builder.Services.AddSingleton<IPointMetadataCache>(sp =>
            new PointMetadataCache(
                sp.GetRequiredService<IPointMetadataDataSource>(),
                sp.GetRequiredService<ILogger<PointMetadataCache>>()));
        return builder;
    }

    /// <summary>
    /// Parquet lake writer (#213): in parquet mode (default since #216) validated telemetry is written
    /// straight to the lake (MinIO) by a durable pull consumer, plus compaction (#217) and retention ILM.
    /// Requires MINIO_ENDPOINT. Mutually exclusive with the cold export worker.
    /// </summary>
    public static IHostApplicationBuilder AddParquetLakeWriter(this IHostApplicationBuilder builder)
    {
        if (!IsParquetWarmStore(builder.Configuration)) return builder;

        var minioEndpoint = builder.Configuration["MINIO_ENDPOINT"];
        if (string.IsNullOrEmpty(minioEndpoint))
            throw new InvalidOperationException(
                "WARM_STORE=parquet requires MINIO_ENDPOINT to be set (the Parquet lake's object store).");

        RegisterMinioBlobStorage(builder, minioEndpoint);
        builder.Services.AddMemoryCache(); // ParquetLakeScan (compactor) caches building discovery
        builder.Services.AddSingleton<IParquetLakeWriter>(sp =>
            new MinioParquetLakeWriter(sp.GetRequiredService<IBlobStorage>()));

        var options = BuildParquetLakeWriterOptions(builder.Configuration);
        builder.Services.AddHostedService(sp => new ParquetLakeWriterWorker(
            sp.GetRequiredService<INatsJSContext>(),
            sp.GetRequiredService<IParquetLakeWriter>(),
            sp.GetRequiredService<ILogger<ParquetLakeWriterWorker>>(),
            options));

        // Compaction (#217): merge per-flush part objects into one compact object per settled building-hour.
        var compactionOptions = BuildCompactionWorkerOptions(builder.Configuration);
        builder.Services.AddHostedService(sp => new CompactionWorker(
            sp.GetRequiredService<IBlobStorage>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<ILogger<CompactionWorker>>(),
            compactionOptions));

        // Retention ILM (#217): LAKE_RETENTION_DAYS > 0 applies an S3/MinIO lifecycle rule. Always
        // registered so unsetting it back to 0 clears a previously-applied rule.
        var retentionDays = int.TryParse(builder.Configuration["LAKE_RETENTION_DAYS"], out var d) && d > 0 ? d : 0;
        builder.Services.AddHostedService(sp => new LakeRetentionHostedService(
            sp.GetRequiredService<IAmazonS3>(),
            retentionDays,
            sp.GetRequiredService<ILogger<LakeRetentionHostedService>>()));
        return builder;
    }

    /// <summary>
    /// TimescaleDB → MinIO cold export worker (legacy warm path). Registered only in timescale mode
    /// with both TIMESCALE_CONNECTION_STRING and MINIO_ENDPOINT set and COLD_EXPORT_ENABLED != false.
    /// </summary>
    public static IHostApplicationBuilder AddColdExportWorker(this IHostApplicationBuilder builder)
    {
        // In parquet mode the lake writer already covers warm+cold; the two are mutually exclusive.
        if (IsParquetWarmStore(builder.Configuration)) return builder;

        var timescaleDsn = builder.Configuration["TIMESCALE_CONNECTION_STRING"];
        var minioEndpoint = builder.Configuration["MINIO_ENDPOINT"];
        var enabled = !string.IsNullOrEmpty(timescaleDsn) && !string.IsNullOrEmpty(minioEndpoint)
            && !string.Equals(builder.Configuration["COLD_EXPORT_ENABLED"], "false", StringComparison.OrdinalIgnoreCase);
        if (!enabled) return builder;

        var coldExportInterval = int.TryParse(builder.Configuration["COLD_EXPORT_INTERVAL"], out var m) ? m : 5;

        RegisterMinioBlobStorage(builder, minioEndpoint!);
        builder.Services.AddSingleton<IExportDataReader>(_ => new NpgsqlExportDataReader(timescaleDsn!));
        builder.Services.AddSingleton<IExportLogRepository>(_ => new NpgsqlExportLogRepository(timescaleDsn!));
        builder.Services.AddSingleton<IColdExportService, NpgsqlMinioExportService>();
        builder.Services.AddHostedService(sp => new ColdExportWorker(
            sp.GetRequiredService<IColdExportService>(),
            sp.GetRequiredService<ILogger<ColdExportWorker>>(),
            coldExportInterval));
        return builder;
    }

    // ── private helpers (moved verbatim from Program.cs) ──────────────────────

    private static void AddCoreConnectors(IHostApplicationBuilder builder)
    {
        builder.Services.AddHostedService(sp => new HvacConnectorWorker(
            Sub("building-os.raw.hvac", "connectorworker-hvac", sp),
            sp.GetRequiredService<INatsPublisher>(),
            sp.GetRequiredService<IPointIdFactory>(),
            sp.GetRequiredService<ILogger<HvacConnectorWorker>>()));

        builder.Services.AddHostedService(sp => new EnvironmentalConnectorWorker(
            Sub("building-os.raw.environmental", "connectorworker-environmental", sp),
            sp.GetRequiredService<INatsPublisher>(),
            sp.GetRequiredService<IPointIdFactory>(),
            sp.GetRequiredService<ILogger<EnvironmentalConnectorWorker>>()));

        builder.Services.AddHostedService(sp => new ElectricConnectorWorker(
            Sub("building-os.raw.electric", "connectorworker-electric", sp),
            sp.GetRequiredService<INatsPublisher>(),
            sp.GetRequiredService<IPointIdFactory>(),
            sp.GetRequiredService<ILogger<ElectricConnectorWorker>>()));

        builder.Services.AddHostedService(sp => new BacnetConnectorWorker(
            Sub("building-os.raw.bacnet", "connectorworker-bacnet", sp),
            sp.GetRequiredService<INatsPublisher>(),
            sp.GetRequiredService<BacnetPointResolver>(),
            sp.GetRequiredService<ILogger<BacnetConnectorWorker>>()));

        builder.Services.AddHostedService(sp => new BehaviorConnectorWorker(
            Sub("building-os.raw.behavior", "connectorworker-behavior", sp),
            sp.GetRequiredService<INatsPublisher>(),
            sp.GetRequiredService<ILogger<BehaviorConnectorWorker>>()));
    }

    private static void AddMqttConnectors(IHostApplicationBuilder builder)
    {
        // Scenario A: Mosquitto MQTT broker — enabled when MQTT_HOST is set.
        var mqttHost = builder.Configuration["MQTT_HOST"]?.Trim();
        if (string.IsNullOrWhiteSpace(mqttHost)) return;

        var mqttPort = int.TryParse(builder.Configuration["MQTT_PORT"], out var mp) ? mp : 1883;
        var mqttUsername = builder.Configuration["MQTT_USERNAME"];
        var mqttPassword = builder.Configuration["MQTT_PASSWORD"];
        var mqttTopicFilter = builder.Configuration["MQTT_TOPIC_FILTER"] ?? "telemetry/#";

        builder.Services.AddHostedService(sp => new MqttIngressWorker(
            sp.GetRequiredService<INatsJSContext>(),
            sp.GetRequiredService<INatsPublisher>(),
            mqttHost,
            mqttPort,
            mqttUsername,
            mqttPassword,
            mqttTopicFilter,
            sp.GetRequiredService<ILogger<MqttIngressWorker>>()));

        builder.Services.AddHostedService(sp => new MqttConnectorWorker(
            Sub("building-os.raw.mqtt", "connectorworker-mqtt", sp),
            sp.GetRequiredService<INatsPublisher>(),
            sp.GetRequiredService<IPointIdFactory>(),
            sp.GetRequiredService<ILogger<MqttConnectorWorker>>()));
    }

    private static void AddHonoConnectors(IHostApplicationBuilder builder)
    {
        // Scenario B: Eclipse Hono AMQP 1.0 Northbound — enabled when HONO_AMQP_HOST is set.
        var honoHost = builder.Configuration["HONO_AMQP_HOST"]?.Trim();
        if (string.IsNullOrWhiteSpace(honoHost)) return;
        var honoAmqpHost = honoHost;

        var honoAmqpPort = int.TryParse(builder.Configuration["HONO_AMQP_PORT"], out var ap) ? ap : 5672;
        var honoAmqpTenant = builder.Configuration["HONO_AMQP_TENANT"] ?? "building-os";
        var honoAmqpUser = builder.Configuration["HONO_AMQP_USER"];
        var honoAmqpPassword = builder.Configuration["HONO_AMQP_PASSWORD"];
        // TLS off by default (plaintext amqp on 5672); HONO_AMQP_TLS=true for amqps (typically 5671).
        var honoAmqpTls = string.Equals(builder.Configuration["HONO_AMQP_TLS"], "true", StringComparison.OrdinalIgnoreCase);

        builder.Services.AddHostedService(sp => new AmqpIngressWorker(
            sp.GetRequiredService<INatsJSContext>(),
            sp.GetRequiredService<INatsPublisher>(),
            honoAmqpHost,
            honoAmqpPort,
            honoAmqpTenant,
            honoAmqpUser,
            honoAmqpPassword,
            honoAmqpTls,
            sp.GetRequiredService<ILogger<AmqpIngressWorker>>()));

        builder.Services.AddHostedService(sp => new HonoConnectorWorker(
            Sub("building-os.raw.hono", "connectorworker-hono", sp),
            sp.GetRequiredService<INatsPublisher>(),
            sp.GetRequiredService<IPointIdFactory>(),
            sp.GetRequiredService<ILogger<HonoConnectorWorker>>()));
    }

    // Sub() builds a NatsMessageSubscription and validates the subject against NatsStreamTopology at
    // startup — a misconfigured subject fails immediately rather than silently dropping messages.
    private static NatsMessageSubscription Sub(string subject, string durableName, IServiceProvider sp)
    {
        NatsStreamTopology.ResolveOrThrow(subject);
        return new NatsMessageSubscription(subject, durableName, sp.GetRequiredService<INatsConnection>());
    }

    internal static bool IsParquetWarmStore(IConfiguration config)
        => WarmStoreMode.IsParquet(config[WarmStoreMode.EnvVar]);

    // Registers the MinIO/S3 blob storage graph (IAmazonS3 + IBlobStorage). Shared by the cold export
    // and parquet lake paths (mutually exclusive), so the singleton is only registered once.
    private static void RegisterMinioBlobStorage(IHostApplicationBuilder builder, string minioEndpoint)
    {
        var minioAccessKey = builder.Configuration["MINIO_ACCESS_KEY"] ?? "buildingos";
        var minioSecretKey = builder.Configuration["MINIO_SECRET_KEY"] ?? "buildingos123";
        builder.Services.AddSingleton<IAmazonS3>(_ =>
        {
            var s3Config = new AmazonS3Config { ServiceURL = minioEndpoint, ForcePathStyle = true };
            return new AmazonS3Client(new BasicAWSCredentials(minioAccessKey, minioSecretKey), s3Config);
        });
        builder.Services.AddSingleton<IBlobStorage>(sp => new MinioBlobStorage(sp.GetRequiredService<IAmazonS3>()));
    }

    // Reads optional PARQUET_* tuning env; unset/invalid keep option defaults. The AckWait floor is
    // derived from the flush interval so an un-acked window always survives redelivery.
    private static ParquetLakeWriterOptions BuildParquetLakeWriterOptions(IConfiguration config)
    {
        var options = new ParquetLakeWriterOptions();

        if (int.TryParse(config["PARQUET_FLUSH_INTERVAL"], out var mins) && mins > 0)
        {
            var flushInterval = TimeSpan.FromMinutes(mins);
            var ackWait = TimeSpan.FromTicks(Math.Max(options.AckWait.Ticks, flushInterval.Ticks * 2));
            options = options with { FlushInterval = flushInterval, AckWait = ackWait };
        }
        if (int.TryParse(config["PARQUET_FLUSH_MAX_ROWS"], out var rows) && rows > 0)
            options = options with { FlushMaxRows = rows };
        if (long.TryParse(config["PARQUET_STREAM_MAX_BYTES"], out var maxBytes) && maxBytes > 0)
            options = options with { StreamMaxBytes = maxBytes };

        return options;
    }

    // Reads optional LAKE_COMPACTION_* env; unset/invalid keep the option defaults.
    private static CompactionWorkerOptions BuildCompactionWorkerOptions(IConfiguration config)
    {
        var options = new CompactionWorkerOptions();
        if (int.TryParse(config["LAKE_COMPACTION_INTERVAL"], out var mins) && mins > 0)
            options = options with { Interval = TimeSpan.FromMinutes(mins) };
        if (int.TryParse(config["LAKE_COMPACTION_SETTLE_MINUTES"], out var settle) && settle > 0)
            options = options with { SettleGrace = TimeSpan.FromMinutes(settle) };
        if (int.TryParse(config["LAKE_COMPACTION_MIN_PARTS"], out var parts) && parts >= 2)
            options = options with { MinParts = parts };
        return options;
    }
}
