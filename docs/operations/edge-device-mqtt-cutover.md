# Edge Device Cutover Runbook: IoT Hub → Hono/EMQX MQTT

## Overview

This runbook covers the phased migration of edge device telemetry from
Azure IoT Hub to Eclipse Hono / EMQX. The migration uses a dual-connection
window to validate parity before full cutover.

## Architecture

```
[Phase 5a - Dual Write]
  Edge Device ──► IoT Hub → Event Hub → Connector → NATS
               └──► Hono/EMQX → Hono North ──► Hono-NATS Bridge → NATS
                                                      (same stream, deduplication via Nats-Msg-Id)

[Phase 5b - Cutover]
  Edge Device ──► Hono/EMQX → Hono-NATS Bridge → NATS
```

## Prerequisites

- Eclipse Hono + EMQX provisioned (Issue #25)
- Hono-NATS bridge deployed (Issue #27)
- Development edge device simulator available at `Tools/development-edge-device/`

---

## Phase 1 — Dual Write with Development Simulator

### 1. Configure dual mode

```bash
export IOTHUB_DEVICE_CONNECTION_STRING="HostName=...;DeviceId=...;SharedAccessKey=..."
export MQTT_HOST="hono.example.com"
export MQTT_PORT="8883"
export MQTT_TLS="1"
export MQTT_USERNAME="simdev001@DEFAULT_TENANT"
export MQTT_PASSWORD="<device-credential>"
export DEVICE_ID="simdev001"
export TELEMETRY_INTERVAL="10"

python Tools/development-edge-device/dual_edge_device.py
```

### 2. Verify dual receipt

Check that BOTH paths receive identical data:
```bash
# IoT Hub path — check Event Hub consumer group
# Hono path — check NATS subject
nats sub "building-os.telemetry.DEFAULT_TENANT.simdev001"
```

### 3. Run parity harness

```bash
# Compare data from both paths (same timestamp window)
# Results should be identical (NATS dedup handles any double-delivery)
```

---

## Phase 2 — Switch MQTT-Only Mode

### Development simulator

```bash
export MQTT_HOST="hono.example.com"
export MQTT_PORT="8883"
export MQTT_TLS="1"
export MQTT_USERNAME="device001@DEFAULT_TENANT"
export MQTT_PASSWORD="<credential>"

python Tools/development-edge-device/mqtt_edge_device.py
```

### Physical devices (vendor coordination required)

Update the device MQTT configuration on physical hardware:
- Old broker: `{iothub}.azure-devices.net:8883` (MQTT over TLS)
- New broker: `hono.example.com:8883` (MQTT over TLS)
- New topic format: `telemetry/{tenant-id}/{device-id}`
- Authentication: device credential provisioned in Hono device registry

Roll out device by device (not all at once):
1. Test lab devices → validate 48h
2. Engineering 2 floor 1 (10 devices) → validate 48h
3. Engineering 2 all floors → validate 1 week
4. All remaining buildings

---

## Phase 3 — Disable IoT Hub

Once all physical devices are confirmed on Hono/EMQX:

1. Stop the `ControlChangeFeedService` (Azure Functions Connector) for each building
2. Verify NATS stream still receives all telemetry
3. Disable IoT Hub devices in Azure Portal (not delete — for rollback)
4. After 2 weeks without incidents, delete IoT Hub devices
5. Decommission IoT Hub Azure resource

---

## Failback Procedure

If Hono/EMQX is unavailable:

### Development simulator

```bash
# Switch back to IoT Hub mode
export IOTHUB_DEVICE_CONNECTION_STRING="..."
python Tools/development-edge-device/iot_edge_device.py
```

### Physical devices

Re-enable the IoT Hub MQTT endpoint in device firmware settings.
Physical devices retain the IoT Hub configuration until it is explicitly overwritten.

Timeline: IoT Hub devices stay provisioned for 3 months after cutover
to ensure failback is possible without reprovisioning.

---

## Monitoring

| Signal | Source | Alert Threshold |
|---|---|---|
| `bridge_messages_received_total` | Hono-NATS bridge Prometheus | 0 msg/5min per device |
| `bridge_messages_published_total{status="error"}` | Hono-NATS bridge | >10/min |
| NATS JetStream pending | NATS monitoring | >1000 pending messages |
| DLQ subject depth | `nats sub building-os.dlq.hono` | any message |
