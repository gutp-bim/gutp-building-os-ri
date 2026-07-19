using BuildingOS.GatewayBridge.Infrastructure;
using BuildingOS.GatewayBridge.Services;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NATS.Client.Core;
using NATS.Client.JetStream;

var builder = WebApplication.CreateBuilder(args);

// ── OpenTelemetry (traces+metrics+logs via OTLP; no-op when endpoint unset) ───
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "building-os-gateway-bridge";
var sampleRatio = double.TryParse(
    builder.Configuration["OTEL_TRACES_SAMPLER_ARG"],
    System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture, out var sr) ? sr : 1.0;
builder.Services.AddOtlpTelemetry(otelServiceName, otlpEndpoint, sampleRatio);
builder.Logging.AddOtlpLogging(otelServiceName, otlpEndpoint);

// ── Kestrel: gRPC needs HTTP/2. Plaintext h2c in-cluster; TLS/mTLS terminates at Envoy (#161) ──
// Short HTTP/2 keepalive pings so a dropped BOWS connection is detected quickly and its
// per-gateway subscription is torn down (plan §3-4); BOWS does periodic reconnect + jitter.
var grpcPort = int.TryParse(builder.Configuration["GRPC_PORT"], out var p) ? p : 8080;
// Must be > 0 (Kestrel throws on zero/negative); a non-positive/unparseable value falls back to the
// default rather than crashlooping the pod.
static TimeSpan PositiveSeconds(string? raw, int defaultSeconds)
    => TimeSpan.FromSeconds(int.TryParse(raw, out var s) && s > 0 ? s : defaultSeconds);
var keepAlivePingDelay = PositiveSeconds(builder.Configuration["GRPC_KEEPALIVE_PING_DELAY_SEC"], 20);
var keepAlivePingTimeout = PositiveSeconds(builder.Configuration["GRPC_KEEPALIVE_PING_TIMEOUT_SEC"], 10);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(grpcPort, listen => listen.Protocols = HttpProtocols.Http2);
    options.Limits.Http2.KeepAlivePingDelay = keepAlivePingDelay;
    options.Limits.Http2.KeepAlivePingTimeout = keepAlivePingTimeout;
});

// ── NATS (core pub/sub for ephemeral egress) ─────────────────────────────────
var natsUrl = builder.Configuration["NATS_URL"] ?? "nats://localhost:4222";
builder.Services.AddSingleton<INatsConnection>(_ => new NatsConnection(new NatsOpts { Url = natsUrl }));
// JetStream context for the KV-backed connection heartbeat (#230); mirrors the ApiServer wiring.
builder.Services.AddSingleton<INatsJSContext>(sp => new NatsJSContext(sp.GetRequiredService<INatsConnection>()));

// ── Egress bridge (control plane) ────────────────────────────────────────────
// Ingress (GatewayIngress / telemetry ingest) now lives in BuildingOS.ConnectorWorker alongside
// the MQTT/AMQP ingress workers; this service is the pure egress control plane.
builder.Services.AddSingleton<GatewayConnectionRegistry>();
builder.Services.AddSingleton<IEgressCommandBus, NatsEgressCommandBus>();

// ── Cross-replica connection heartbeat (#230 Phase 2②, ADR-0004) ─────────────
// Each live egress stream refreshes a per-gateway KV entry on GATEWAY_HEARTBEAT_INTERVAL_SEC; the
// bucket's TTL (GATEWAY_HEARTBEAT_TTL_SEC, default 3× the interval) expires it if a replica dies. The
// ApiServer reads it to show true connected/disconnected. Best-effort — never affects command routing.
var heartbeatInterval = PositiveSeconds(builder.Configuration["GATEWAY_HEARTBEAT_INTERVAL_SEC"], 10);
var heartbeatTtl = PositiveSeconds(builder.Configuration["GATEWAY_HEARTBEAT_TTL_SEC"], NatsKvGatewayConnectionStore.DefaultTtlSeconds);
if (heartbeatTtl <= heartbeatInterval)
{
    // TTL must exceed the beat, or a live gateway's entry expires between beats and flaps to
    // disconnected. Warn loudly (config error) rather than silently mis-report the fleet.
    Console.Error.WriteLine(
        $"[WARN] GATEWAY_HEARTBEAT_TTL_SEC ({heartbeatTtl.TotalSeconds:0}s) <= GATEWAY_HEARTBEAT_INTERVAL_SEC " +
        $"({heartbeatInterval.TotalSeconds:0}s); connected state may flap. Set TTL to >= 2-3x the interval.");
}
builder.Services.AddSingleton<IGatewayConnectionStatusStore>(sp => new NatsKvGatewayConnectionStore(
    sp.GetRequiredService<INatsJSContext>(),
    sp.GetRequiredService<ILogger<NatsKvGatewayConnectionStore>>(),
    heartbeatTtl));
builder.Services.AddSingleton(new GatewayHeartbeatOptions(heartbeatInterval, Environment.MachineName));

builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<GatewayEgressService>();
app.MapGet("/", () => "BuildingOS.GatewayBridge — gRPC GatewayEgress (Connect)");

app.Run();
