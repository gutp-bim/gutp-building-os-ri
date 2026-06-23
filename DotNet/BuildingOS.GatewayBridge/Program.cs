using BuildingOS.GatewayBridge.Infrastructure;
using BuildingOS.GatewayBridge.Services;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NATS.Client.Core;

var builder = WebApplication.CreateBuilder(args);

// ── OpenTelemetry (traces+metrics+logs via OTLP; no-op when endpoint unset) ───
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "building-os-gateway-bridge";
builder.Services.AddOtlpTelemetry(otelServiceName, otlpEndpoint);
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

// ── Egress bridge (control plane) ────────────────────────────────────────────
// Ingress (GatewayIngress / telemetry ingest) now lives in BuildingOS.ConnectorWorker alongside
// the MQTT/AMQP ingress workers; this service is the pure egress control plane.
builder.Services.AddSingleton<GatewayConnectionRegistry>();
builder.Services.AddSingleton<IEgressCommandBus, NatsEgressCommandBus>();

builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<GatewayEgressService>();
app.MapGet("/", () => "BuildingOS.GatewayBridge — gRPC GatewayEgress (Connect)");

app.Run();
