#!/usr/bin/env python3
"""
MQTT Edge Device Simulator for Hono / EMQX
Sends telemetry to Eclipse Hono via MQTT (replaces IoT Hub transport).

Environment variables:
    MQTT_HOST           MQTT broker host (default: localhost)
    MQTT_PORT           MQTT broker port (default: 1883 / 8883 for TLS)
    MQTT_TLS            Enable TLS: "1" (default: 0)
    MQTT_USERNAME       MQTT username (Hono: device@tenant-id)
    MQTT_PASSWORD       MQTT password / device credential
    DEVICE_ID           Device ID (default: simulated-device-001)
    TENANT_ID           Hono tenant ID (default: DEFAULT_TENANT)
    TELEMETRY_INTERVAL  Seconds between messages (default: 10)
    BUILDING_ID         Building identifier for payload (default: ENG2)
    POINT_ID            Point ID for payload (default: sim-point-001)
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import ssl
import time
import uuid
from dataclasses import dataclass, field
from typing import Any

import asyncio_mqtt as aiomqtt

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)


@dataclass
class MqttConfig:
    host: str = field(default_factory=lambda: os.environ.get("MQTT_HOST", "localhost"))
    port: int = field(default_factory=lambda: int(os.environ.get("MQTT_PORT", "1883")))
    tls: bool = field(default_factory=lambda: os.environ.get("MQTT_TLS", "0") == "1")
    username: str = field(default_factory=lambda: os.environ.get("MQTT_USERNAME", ""))
    password: str = field(default_factory=lambda: os.environ.get("MQTT_PASSWORD", ""))
    device_id: str = field(default_factory=lambda: os.environ.get("DEVICE_ID", "simulated-device-001"))
    tenant_id: str = field(default_factory=lambda: os.environ.get("TENANT_ID", "DEFAULT_TENANT"))
    interval: float = field(default_factory=lambda: float(os.environ.get("TELEMETRY_INTERVAL", "10")))
    building_id: str = field(default_factory=lambda: os.environ.get("BUILDING_ID", "ENG2"))
    point_id: str = field(default_factory=lambda: os.environ.get("POINT_ID", "sim-point-001"))


class MqttEdgeDevice:
    """
    Sends telemetry to Eclipse Hono via MQTT.

    Hono MQTT topic convention:
      telemetry/{tenant-id}/{device-id}
    or (when username already encodes tenant):
      telemetry
    """

    def __init__(self, config: MqttConfig) -> None:
        self._cfg = config
        # Hono northbound topic: publish telemetry on this topic
        self._topic = f"telemetry/{self._cfg.tenant_id}/{self._cfg.device_id}"

    def _build_payload(self, seq: int) -> dict[str, Any]:
        return {
            "id": str(uuid.uuid4()),
            "datetime": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "point_id": self._cfg.point_id,
            "building": self._cfg.building_id,
            "device_id": self._cfg.device_id,
            "name": "indoor_temp",
            "value": round(20.0 + (seq % 10) * 0.5, 2),
            "seq": seq,
        }

    async def run(self) -> None:
        tls_context = ssl.create_default_context() if self._cfg.tls else None

        connect_kwargs: dict[str, Any] = {
            "hostname": self._cfg.host,
            "port": self._cfg.port,
        }
        if self._cfg.username:
            connect_kwargs["username"] = self._cfg.username
            connect_kwargs["password"] = self._cfg.password
        if tls_context:
            connect_kwargs["tls_context"] = tls_context

        logger.info(
            "Connecting to MQTT broker %s:%d (TLS=%s, topic=%s)",
            self._cfg.host, self._cfg.port, self._cfg.tls, self._topic
        )

        seq = 0
        async with aiomqtt.Client(**connect_kwargs) as client:
            logger.info("Connected. Sending telemetry every %.1fs", self._cfg.interval)
            while True:
                payload = self._build_payload(seq)
                await client.publish(
                    self._topic,
                    payload=json.dumps(payload).encode(),
                    qos=1,
                )
                logger.info("Published seq=%d to %s: value=%.2f", seq, self._topic, payload["value"])
                seq += 1
                await asyncio.sleep(self._cfg.interval)


async def main() -> None:
    cfg = MqttConfig()
    if not cfg.username:
        logger.warning("MQTT_USERNAME not set — broker may reject unauthenticated connection")
    device = MqttEdgeDevice(cfg)
    try:
        await device.run()
    except KeyboardInterrupt:
        logger.info("Stopped by user")


if __name__ == "__main__":
    asyncio.run(main())
