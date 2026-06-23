# BuildingOS.GatewayBridge

gRPC ⇄ NATS **egress** bridge for the external (building-edge) BOWS connector: a stateless device-control
plane hosting the `GatewayEgress` service. Telemetry **ingress** (`GatewayIngress`) is hosted by
BuildingOS.ConnectorWorker alongside the MQTT/AMQP ingress workers (gated on `GRPC_INGRESS_PORT`) — see
`docs/gateway-bridge-ingress-egress-split.md`. Background: `docs/oss-egress-gateway-bridge-plan.md` §3.

## Scaling & replica routing (stateless)

The bridge holds **no persistent state**. A replica only subscribes to the NATS per-gateway subject
`building-os.control.request.gw.{gatewayId}` for the gateways whose egress stream it currently holds.
Because routing to the holding replica is done by NATS (per-gateway subject fan-in), the gRPC L7 LB
may place a BOWS connection on any replica — a command published to a gateway's subject always reaches
the replica holding that gateway's stream.

Failover: if a replica dies, its subscriptions vanish with it; BOWS reconnects (to any replica) and
that replica re-subscribes. There is nothing to drain or migrate — the durable spine is NATS.

## Connection health

| Concern | Where | Mechanism |
|---|---|---|
| Detect dead BOWS connections fast | **bridge (server)** | HTTP/2 keepalive pings (Kestrel `Http2.KeepAlivePingDelay`/`KeepAlivePingTimeout`). A dead connection is closed and its per-gateway subscription torn down. |
| Even connection distribution / avoid thundering herd | **BOWS (client)** | periodic reconnect with **jitter** (long-lived streams are recycled at randomized intervals so they rebalance across replicas). |

## Environment variables

| Variable | Description | Default |
|----------|-------------|---------|
| `NATS_URL` | NATS connection URL | `nats://localhost:4222` |
| `GRPC_PORT` | gRPC (HTTP/2 h2c) listen port; TLS/mTLS terminates at Envoy (see #161) | `8080` |
| `GRPC_KEEPALIVE_PING_DELAY_SEC` | HTTP/2 keepalive ping delay (idle before a ping); must be > 0, else default | `20` |
| `GRPC_KEEPALIVE_PING_TIMEOUT_SEC` | HTTP/2 keepalive ping ack timeout before closing the connection; must be > 0, else default | `10` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP endpoint (traces+metrics+logs). No-op when unset. | — |
| `OTEL_SERVICE_NAME` | OTLP `service.name` | `building-os-gateway-bridge` |

> BOWS reconnect + jitter is a **client-side** responsibility (the bbc-sim BOWS connector); the bridge
> cannot force it. Recommended: recycle each gateway stream every N minutes ± random jitter.
