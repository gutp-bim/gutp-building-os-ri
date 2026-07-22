# GatewayBridge — north-south ingress / LB / mTLS (infra)

Deployment & networking for `BuildingOS.GatewayBridge` so the cluster-external (building-edge) BOWS
connector can open gRPC streams into the cluster. Companion to
`docs/project/oss-egress-gateway-bridge-plan.md` §3-5 (the GatewayBridge service itself is documented in its
in-project README, added with the replica-routing slice).

> **HITL**: the live pieces below (ingress reachability, cert issuance, external BOWS connect) require
> a cluster and an infra-direction review. The Helm/ArgoCD/cert-manager manifests are authored and
> render, but on-cluster verification is a human step.

## Load-balancing policy

- **Connection-level (L4/L7) LB is sufficient.** A gateway opens exactly one long-lived gRPC stream;
  the ingress may place that connection on **any** GatewayBridge replica.
- **Command delivery is guaranteed by NATS, not by the LB.** The replica holding a gateway's stream
  subscribes to `building-os.control.request.gw.{gatewayId}`. The ApiServer publishes a command to
  that per-gateway subject, so it reaches the holding replica regardless of which replica the LB chose.
  This is what lets the bridge stay **stateless** and scale horizontally.
- No sticky sessions / consistent hashing at the LB are required.

## Ingress controller

The repo standardizes on **Traefik** (`IngressRoute`, `scheme: h2c` for gRPC L7 LB + health checks),
already used by `templates/ingress.yaml`. The plan named "Envoy系 (Contour/Emissary)"; these are
**drop-in alternatives** — any L7 proxy that does HTTP/2 (h2c) gRPC LB works, because reachability is
handled by NATS. Choosing Traefik keeps the bridge consistent with the deployed stack.
**Decision for review:** keep Traefik vs. adopt Contour/Envoy for the edge.

## mTLS (cert-manager)

- Server cert for the ingress host: cert-manager `Certificate` → `serverSecretName`.
- Client auth: Traefik `TLSOption` with `clientAuth.clientAuthType: RequireAndVerifyClientCert` and
  the client CA in `clientCaSecretName`. Only BOWS clients presenting a cert signed by that CA connect.
- BOWS client certs are issued/rotated out-of-band (or by cert-manager) and provisioned to the edge.
- **Telemetry ingress identity binding (#296)**: for the ConnectorWorker `GatewayIngress` route, add a
  Traefik `passTLSClientCert` middleware that injects the verified cert subject (SAN/CN) as the trusted
  header `X-Gateway-Id` (matching `GRPC_INGRESS_GATEWAY_ID_HEADER`), and set
  `GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY=true` on the worker so a frame's `gateway_id` must match it.
  The header MUST be stripped on every untrusted path and the route reachable only via this mTLS
  ingress. The app-side check is unit-tested; the on-cluster `passTLSClientCert`→header wiring for this
  route is **HITL** (the connector-worker chart passes both env vars through `connectorWorker.env`
  generically, so only the IngressRoute middleware needs authoring).

## Helm / ArgoCD wiring

- Standalone chart `kubernetes/helm/gateway-bridge` (Deployment + Service[h2c] + optional HPA +
  optional Traefik IngressRoute/TLSOption + optional cert-manager Certificate), mirroring the other
  per-component charts. Also added to the monolithic `building-os` chart
  (`templates/gateway-bridge.yaml`, `gatewayBridge.*` values) for all-in-one installs.
- ArgoCD: `argocd/apps/gateway-bridge-utokyo-eng2.yaml` + `argocd/values/gateway-bridge-utokyo-eng2.yaml`.
- OTel: `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_SERVICE_NAME` env (traces+metrics+logs; no-op when unset).
- Disabled by default (`enabled: false`) — opt-in once the infra review is done.

## On-cluster verification checklist (HITL)

1. Build & push the `gateway-bridge` image; set the image repo in the ArgoCD values.
2. Provision the cert-manager issuer + client CA secret; enable `ingress.mtls.*`.
3. `enabled: true`, sync the ArgoCD app; confirm pods Ready and the Service is h2c.
4. From an external BOWS (or `grpcurl` with a client cert), open a `GatewayEgress.Connect` stream
   through the ingress host and confirm mTLS is enforced (connection without a client cert is rejected).
5. End-to-end downlink validation is tracked by #163 (bbc-sim integration).
