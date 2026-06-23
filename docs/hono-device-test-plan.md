# Hono Device Test Plan

This plan validates development edge devices against the Hono/EMQX OSS path.

## Development Edge Device

Use `Tools/development-edge-device/mqtt_edge_device.py` for MQTT publishing and
`Tools/development-edge-device/dual_edge_device.py` for dual-path comparison.

Required inputs:

| Variable | Purpose |
|---|---|
| `MQTT_HOST` | EMQX/Hono MQTT endpoint |
| `MQTT_PORT` | MQTT listener port |
| `MQTT_USERNAME` | Device auth ID |
| `MQTT_PASSWORD` | Device credential |
| `DEVICE_ID` | Hono device ID |

## Test Scenarios

1. Register a test tenant and device in Hono.
2. Publish a valid telemetry payload from `mqtt_edge_device.py`.
3. Confirm the Hono bridge publishes a NATS raw subject.
4. Confirm ConnectorWorker normalizes telemetry.
5. Confirm malformed payloads land in `building-os.dlq.hono`.
6. Run `dual_edge_device.py` and compare downstream outputs.

## Acceptance Evidence

Attach the following to the PR:

- Hono tenant/device credential creation log with secrets redacted.
- MQTT publish log.
- Hono bridge log showing NATS publish.
- Parity comparison output for at least one telemetry payload.

## Rollback

For each tested device, record the previous IoT Hub connection setting before
switching to MQTT. To roll back, restore that setting, disable the Hono
credential, and verify telemetry resumes through the previous path.

