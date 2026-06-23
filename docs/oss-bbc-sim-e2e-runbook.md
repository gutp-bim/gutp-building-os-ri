# bbc-sim / BOWS downlink E2E runbook (#163)

End-to-end validation of device control from the Web UI down to a BACnet WriteProperty on the bbc-sim
B-BC simulator, via the GatewayBridge. Companion to `docs/oss-egress-gateway-bridge-plan.md` §5.

> **HITL / cross-repo**: the BOWS downlink (gRPC subscribe → BACnet WriteProperty → result) is a
> future task in [bacnet-sim-gateway](https://github.com/takashikasuya/bacnet-sim-gateway) —
> tracked by **takashikasuya/bacnet-sim-gateway#67**. The full live E2E below cannot pass until that
> lands; this repo's in-cluster leg is verified by the integration test
> `BuildingOS.IntegrationTest/Tests/GatewayBridgeEgressNatsTest.cs`.

## Full path

```
Web/REST  →  ApiServer (POST /points/{id}/control)
          →  ControlTypeResolver → connectionType=bacnet-sim, ControlType=BacnetSim, GatewayId
          →  NATS  building-os.control.request.gw.{gatewayId}   (per-gateway subject)
          →  GatewayBridge (the replica holding the gateway's GatewayEgress stream)
          →  gRPC EgressDown{ControlCommand}  →  BOWS
          →  BACnet/IP WriteProperty           →  bbc-sim B-BC  (present-value changes)
          →  BOWS  gRPC EgressUp{ControlResult}
          →  GatewayBridge → NATS building-os.control.result.{controlId}
          →  ApiServer NatsControlResultBus → gRPC PointControlService.WaitForResult → Web
```

## Identity alignment

- `localId = {tenant}/{deviceId}` on the bbc-sim/BOWS side.
- `point_id` is resolved by Building OS via OxiGraph; `Point.*Bacnet` (DeviceIdBacnet / ObjectType /
  InstanceNo) supplies the WriteProperty target. **Note**: populating `Point.*Bacnet` from the bbc-sim
  pointlist into the OSS twin is a prerequisite (see #153 / bbc-sim pointlist sync) — until then the
  BacnetSim body builder returns null and the command is rejected with 400.

## Prerequisites

1. #159/#160/#162 merged (GatewayBridge egress+ingress, replica routing).
2. #161 infra applied: GatewayBridge deployed, the north-south gRPC L7 ingress (Traefik per #161;
   the plan §3-5 names Envoy/Contour as an equivalent alternative) + cert-manager mTLS, ArgoCD synced.
3. `GatewayConnectionTypes:Map.{gatewayId}=bacnet-sim` configured on ApiServer.
4. bbc-sim running with BOWS downlink implemented (bbc-sim#67); BOWS holds a client cert (mTLS).
5. The point's BACnet identity present in the OSS twin (pointlist sync).

## Steps

1. Start bbc-sim (B-BC) and BOWS; confirm BOWS opens a `GatewayEgress.Connect` stream through the
   north-south gRPC ingress (Traefik per #161) and the mTLS handshake succeeds.
2. From the Web UI (or `curl`/`grpcurl`), `POST /points/{pointId}/control` with a target value.
3. Confirm a `202 Accepted` with a `controlId`.
4. Observe the WriteProperty hit bbc-sim and the **present-value change** to the target.
5. Confirm the result is delivered to the UI via `WaitForResult` (success).

## Acceptance (live, HITL)

- [ ] WriteProperty executed on bbc-sim via BOWS; present-value changes.
- [ ] Result delivered to Web via `WaitForResult`.
- [ ] `localId` / `point_id` alignment correct.
- [ ] Synced with bacnet-sim-gateway#67 (BOWS downlink).

## In-repo verification (AFK, available now)

```bash
cd DotNet
dotnet test BuildingOS.IntegrationTest --filter "FullyQualifiedName~GatewayBridgeEgressNats"   # Docker required
```
Proves: command published to the per-gateway subject reaches the egress subscription and maps to a
`ControlCommand`; a result published back lands on the result subject for `WaitForResult`.
