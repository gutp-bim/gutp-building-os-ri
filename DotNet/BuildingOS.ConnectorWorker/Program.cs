using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.ConnectorWorker.Infrastructure.Health;
using BuildingOS.ConnectorWorker.Startup;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// ── Host ────────────────────────────────────────────────────────────────────
// The worker always runs on a WebApplication so it can expose a /health surface (liveness +
// NATS-readiness, #145) on a dedicated internal HEALTH_PORT. The gRPC ingress (GatewayIngress) stays
// gated on GRPC_INGRESS_PORT: its h2c listener and service are added ONLY when set, so OSS/local/CI
// keep the external ingest surface closed by configuration while health is always available on its
// own port. The listener ports must be resolved BEFORE the host builder configures Kestrel, so they
// are read from a tiny bootstrap configuration over env + command-line only (the host builder later
// layers in appsettings etc.; ports are intentionally kept to these two unambiguous sources).
var bootstrapConfig = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();
// WORKER_ROLE (#400) selects the capability set. It is read from the same bootstrap sources as the
// ports because it decides whether the gRPC listener is opened at all: only ingest-capable roles get
// the port, so a lake/control replica inheriting GRPC_INGRESS_PORT from a shared config neither binds
// a dead listener nor trips the port-collision check below.
var workerRole = WorkerRoles.Parse(bootstrapConfig[WorkerRoles.EnvVar]);
var grpcIngressPort = workerRole.RunsTelemetryIngress()
    ? ResolveGrpcIngressPort(bootstrapConfig["GRPC_INGRESS_PORT"])
    : null;
var healthPort = ResolvePort(bootstrapConfig["HEALTH_PORT"], 8081);

var builder = WebApplication.CreateBuilder(args);
ConfigureKestrelListeners(builder, healthPort, grpcIngressPort);

// Service graph for this role, grouped by capability (see ConnectorWorkerServiceCollectionExtensions).
// WORKER_ROLE unset / "all" registers exactly what the worker registered before the switch existed.
builder.AddConnectorWorkerCapabilities(workerRole, grpcIngressPort);

// Health (#145): liveness = the process serves HTTP (no checks); readiness = the NATS connection is
// Open so the worker can actually consume/publish. The system-status fan-out (#144) targets /health.
builder.Services.AddHealthChecks()
    .AddCheck<NatsReadinessHealthCheck>("nats", tags: ["ready"]);

var app = builder.Build();
// Liveness: no checks — 200 as long as the process serves HTTP (orchestrator restart signal).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
// Readiness + overall /health: the ready-tagged checks (NATS connection Open).
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
if (grpcIngressPort is not null)
    app.MapGrpcService<GatewayIngressService>();
LogStartup(app.Services, workerRole, grpcIngressPort);
app.Run();

// ── Host configuration helpers ────────────────────────────────────────────────

// Returns the configured ingress port, or null when GRPC_INGRESS_PORT is unset / non-positive
// (the gate that keeps the gRPC ingress surface closed).
static int? ResolveGrpcIngressPort(string? raw)
    => int.TryParse(raw, out var port) && port > 0 ? port : null;

// A positive port, or the fallback when unset / non-positive / unparseable.
static int ResolvePort(string? raw, int fallback)
    => int.TryParse(raw, out var port) && port > 0 ? port : fallback;

static void ConfigureKestrelListeners(WebApplicationBuilder builder, int healthPort, int? grpcIngressPort)
{
    // Fail fast with a clear message on a port collision rather than an opaque Kestrel bind error.
    if (grpcIngressPort == healthPort)
        throw new InvalidOperationException(
            $"HEALTH_PORT and GRPC_INGRESS_PORT must differ (both = {healthPort}).");

    // Explicit ListenAnyIP overrides ASPNETCORE_URLS, so these are the ONLY listeners.
    var keepAlivePingDelay = PositiveSeconds(builder.Configuration["GRPC_KEEPALIVE_PING_DELAY_SEC"], 20);
    var keepAlivePingTimeout = PositiveSeconds(builder.Configuration["GRPC_KEEPALIVE_PING_TIMEOUT_SEC"], 10);
    builder.WebHost.ConfigureKestrel(options =>
    {
        // Always-on internal health surface (HTTP/1.1).
        options.ListenAnyIP(healthPort, listen => listen.Protocols = HttpProtocols.Http1);

        if (grpcIngressPort is int port)
        {
            // gRPC needs HTTP/2. Plaintext h2c in-cluster; TLS/mTLS terminates at the ingress
            // (Traefik/Envoy). Short HTTP/2 keepalive pings so a dropped BOWS connection is detected fast.
            options.ListenAnyIP(port, listen => listen.Protocols = HttpProtocols.Http2);
            options.Limits.Http2.KeepAlivePingDelay = keepAlivePingDelay;
            options.Limits.Http2.KeepAlivePingTimeout = keepAlivePingTimeout;
        }
    });

    // Must be > 0 (Kestrel throws on zero/negative); a non-positive/unparseable value falls back to
    // the default rather than crashlooping the pod.
    static TimeSpan PositiveSeconds(string? raw, int defaultSeconds)
        => TimeSpan.FromSeconds(int.TryParse(raw, out var s) && s > 0 ? s : defaultSeconds);
}

// Startup summary — emitted through the real logging pipeline (also exported to Loki via OTel) so
// operators can immediately tell which ingress scenarios are active and what HONO_AMQP_HOST resolved
// to inside the process (see issue #131).
void LogStartup(IServiceProvider services, WorkerRole role, int? ingressGrpcPort)
{
    var config = services.GetRequiredService<IConfiguration>();
    var mqttHost = config["MQTT_HOST"]?.Trim();
    var honoHost = config["HONO_AMQP_HOST"]?.Trim();
    var honoHostRaw = config["HONO_AMQP_HOST"];

    // #296/#292: make the ingress identity-binding and hierarchy-check states observable so an
    // operator can spot the "listener up but enforcement off" misconfiguration from the logs.
    var identity = services.GetService<IngressIdentityOptions>();
    var hierarchy = services.GetService<IngressHierarchyOptions>();
    var grpcDesc = ingressGrpcPort is int p
        ? $"enabled port={p}, identity-binding={(identity?.Enforce == true ? $"enforced(header={identity.HeaderName})" : "off")}" +
          $", hierarchy-check={(hierarchy?.Enforce == true ? "enforced" : "off")}"
        : role.RunsTelemetryIngress()
            ? "disabled (GRPC_INGRESS_PORT unset)"
            : $"disabled (role={role.ToString().ToLowerInvariant()})";

    // The MQTT/Hono ingress workers belong to the protocol-connector capability, so a role that does
    // not run them must not report "enabled" just because the host variable is present in the config.
    string IngressDesc(string? host, string unsetReason) =>
        !role.RunsProtocolConnectors() ? $"disabled (role={role.ToString().ToLowerInvariant()})"
        : string.IsNullOrWhiteSpace(host) ? unsetReason
        : $"enabled host={host}";

    services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("BuildingOS.ConnectorWorker.Startup")
        .LogInformation(
            "Connector role: {Role} — gRPC ingress: {Grpc}, MQTT: {Mqtt}, Hono/AMQP: {Hono} (HONO_AMQP_HOST='{HonoHostRaw}')",
            role.ToString().ToLowerInvariant(),
            grpcDesc,
            IngressDesc(mqttHost, "disabled (MQTT_HOST unset)"),
            IngressDesc(honoHost, "disabled (HONO_AMQP_HOST unset)"),
            honoHostRaw);
}
