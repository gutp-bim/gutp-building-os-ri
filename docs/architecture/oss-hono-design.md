# OSS Device Connectivity Design — MQTT broker (optional) + AMQP 1.0 northbound

This document is the HITL review artifact for replacing Azure IoT Hub device
connectivity in the OSS edition. It covers two **independent, both-optional** ingress
transports and the migration off IoT Hub. (Originally titled "Hono + EMQX"; EMQX was a
candidate broker that was never shipped — see "Broker positioning" below.)

> Issue #25. Related: `docs/architecture/oss-nats-design.md` (the NATS core), `docs/project/hono-device-test-plan.md`
> (device-side test plan), `docs/operations/edge-device-mqtt-cutover.md` (cutover runbook).

> **成熟度と更新頻度（リスク）**: Eclipse Hono のプロジェクト状態は Mature だが、公式の最新リリースは **2023-11 の
> 2.4.1 / 2.3.2** と更新頻度が低い。2026 年時点で新規の中核コンポーネントには据えず、**正本取り込み経路は gRPC
> GatewayIngress（#181）**とし、Hono / AMQP northbound は **必要時のみ前段に置く optional 経路**に限定する。
> **ブローカ選定**: EMQX は現行 **Business Source License 1.1** で第三者向け hosted/embedded に制限があるため不採用。
> MQTT ブローカは **Mosquitto（EPL/EDL）/ VerneMQ（Apache 2.0）** を採用する。

## Transports and where they sit

Building OS ingests device telemetry over NATS subjects (`building-os.raw.*` →
`building-os.validated.telemetry`). Two **edge transports** feed those subjects, and the
NATS core does not depend on either being present:

| Scenario | Transport | Worker(s) | Raw subject |
|---|---|---|---|
| **A** | MQTT broker (devices publish MQTT) | `MqttIngressWorker` + `MqttConnectorWorker` (enabled by `MQTT_HOST`) | `building-os.raw.mqtt` |
| **B** | AMQP 1.0 northbound server | `AmqpIngressWorker` + `HonoConnectorWorker` (enabled by `HONO_AMQP_HOST`) | `building-os.raw.hono` |

Both are **off by default**: a worker only starts its ingress when the corresponding host
env var is set. The canonical, always-on ingest is the **gRPC GatewayIngress** (point-id
based, `docs/architecture/oss-nats-design.md`); MQTT/AMQP are for devices that do not speak that path.

## Broker positioning (decision #25)

- **The MQTT broker is optional and is NOT part of the base stack.** The base `docker-compose.oss.yaml`
  brings up the NATS core, storage, auth and observability — no broker. Scenario A is opt-in via the
  `mqtt` compose profile (mirroring the `timescale` / `assistant` opt-in profiles).
- **Recommended broker: Eclipse Mosquitto** (lightweight, permissive license). It is what the `mqtt`
  profile ships. **EMQX** and **VerneMQ** are documented **scale-out alternatives**, not shipped — pick
  them only when Mosquitto's single-node throughput/clustering is insufficient. (VerneMQ is the
  license-aware alternative noted in the architecture review; EMQX's broker license should be reviewed
  before adopting it as the default.)
- **Scenario B targets a generic AMQP 1.0 server, not a specific product.** `AmqpIngressWorker` is an
  AMQP 1.0 consumer; it can connect to any AMQP 1.0 broker — **RabbitMQ** (with the AMQP 1.0 plugin),
  **Apache Qpid**, or **Eclipse Hono's AMQP Northbound**. Hono is the reference for IoT-Hub-style device
  bridging (tenant + device registry + DPS-equivalent), but the **transport contract is plain AMQP 1.0**,
  so describe and provision it as "an AMQP server" (e.g. RabbitMQ) rather than as Hono specifically.

  > Implementation note / current coupling: the consumer's source addresses are `/telemetry/{tenant}`
  > and `/events/{tenant}` and it reads the device id from the Hono `orig_address` application property.
  > A non-Hono AMQP server (RabbitMQ etc.) must either present those addresses or have the address /
  > device-id-extraction made configurable (tracked as a follow-up, out of scope for this design).

## Provisioning (Scenario B)

Tenant: `building-os`

Device registry fields:

| Field | Source | Notes |
|---|---|---|
| `tenantId` | Deployment | `building-os` for local and OSS default |
| `deviceId` | Existing edge inventory | Stable device identifier |
| `authId` | Existing connection identity | MQTT username or certificate subject |
| `credentialType` | Migration choice | `hashed-password` or `x509-cert` |
| `buildingId` / `floorId` / `spaceId` | Digital twin metadata | Used for subject enrichment |

Provisioning scripts create tenants, devices, and credentials through the chosen AMQP server's
management API (Hono device registry API, or RabbitMQ's management API / definitions import for a
RabbitMQ-based deployment). Production credentials must be generated outside source control.

## MQTT credential mapping (IoT Hub → OSS)

| IoT Hub source | OSS target |
|---|---|
| Device ID | Broker device id / MQTT client id |
| Shared access key | Hashed MQTT password or replaced credential |
| DPS enrollment group | Tenant/credential provisioning batch |
| X.509 thumbprint | X.509 credential entry |
| Device twin tags | Device registry / digital-twin metadata |

## TLS and X.509

Local development may use password credentials (Mosquitto port 1883, `allow_anonymous false`).
Production should use TLS for all MQTT transport (Mosquitto 8883 is TLS-ready in
`oss-stack/mosquitto/mosquitto.conf`) and prefer mTLS/X.509 for managed edge devices. Certificate
rotation must be tested before cutover.

## Northbound → NATS ingress

AMQP 1.0 northbound telemetry is consumed by `BuildingOS.ConnectorWorker` via `AmqpIngressWorker`
(enabled via `HONO_AMQP_HOST` — the env keeps the `HONO_` prefix for compatibility but the target is
any AMQP 1.0 server). Accepted messages are published to `building-os.raw.hono` and normalised by
`HonoConnectorWorker` into `building-os.validated.telemetry`.

> `MqttIngressWorker` (enabled via `MQTT_HOST`) is a **separate** ingress path for an MQTT broker
> (Mosquitto, Scenario A) and publishes to `building-os.raw.mqtt`. Do not set `MQTT_HOST` to enable the
> AMQP path; use `HONO_AMQP_HOST`.

> The former standalone `Tools/hono-nats-bridge` Python service has been removed; it was superseded by
> the in-process `AmqpIngressWorker` in ConnectorWorker.

## Cutover

1. Register tenant and test devices on the chosen AMQP server / MQTT broker.
2. Run dual edge publishing to IoT Hub and the OSS transport.
3. Compare downstream telemetry through the parity harness.
4. Move production devices in batches.
5. Keep IoT Hub credentials valid for the rollback window.

## Rollback

Rollback is device-level: restore the IoT Hub connection string or DPS enrollment, disable the OSS
credential, and confirm telemetry returns through the previous path. Do not delete OSS registry records
until the rollback window has expired.

## HITL Sign-Off

Operations and device owners must confirm. **Two of these require live edge testing and cannot be
signed off from this document** — they stay open until the device-side test plan
(`docs/project/hono-device-test-plan.md`) is executed.

- [x] **Broker positioning**: MQTT broker is optional / not in the base stack; Mosquitto recommended,
      EMQX/VerneMQ as scale-out alternatives; northbound described as a generic AMQP 1.0 server (e.g.
      RabbitMQ). _Signed off 2026-06-15._
- [x] **Provisioning and credential storage** model is acceptable (registry fields + credentials
      generated outside source control). _Signed off 2026-06-15 (design)._
- [x] **TLS/mTLS requirements**: TLS for transport, mTLS/X.509 preferred for managed edge; rotation
      tested before cutover. _Signed off 2026-06-15 (design); cert rotation drill is part of the live test._
- [ ] **Dual-connect and rollback procedures are tested** against a live edge device (requires
      execution of `docs/project/hono-device-test-plan.md`).
- [ ] **Device-side test results are attached to the PR** (`Tools/development-edge-device/` against the
      chosen broker/AMQP server).
