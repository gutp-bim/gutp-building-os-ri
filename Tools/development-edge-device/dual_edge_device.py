#!/usr/bin/env python3
"""
Dual-mode Edge Device Simulator
Sends telemetry to BOTH Azure IoT Hub and Hono/EMQX simultaneously.
Used during the dual-write migration window.

Environment variables (all from iot_edge_device.py and mqtt_edge_device.py):
    IOTHUB_DEVICE_CONNECTION_STRING  IoT Hub connection string
    MQTT_HOST, MQTT_PORT, ...        Hono MQTT endpoint config
    DUAL_MODE                        "1" to enable dual mode (default: detect from env)
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import time
import uuid
from typing import Any

logger = logging.getLogger(__name__)


def _build_telemetry(seq: int, device_id: str, building_id: str, point_id: str) -> dict[str, Any]:
    return {
        "id": str(uuid.uuid4()),
        "datetime": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "point_id": point_id,
        "building": building_id,
        "device_id": device_id,
        "name": "indoor_temp",
        "value": round(20.0 + (seq % 10) * 0.5, 2),
        "seq": seq,
    }


async def _send_to_iothub(connection_string: str, payload: dict) -> None:
    try:
        from azure.iot.device.aio import IoTHubDeviceClient
        from azure.iot.device import Message
        client = IoTHubDeviceClient.create_from_connection_string(connection_string)
        await client.connect()
        msg = Message(json.dumps(payload))
        msg.content_type = "application/json"
        msg.content_encoding = "utf-8"
        await client.send_message(msg)
        await client.disconnect()
        logger.debug("IoT Hub: sent seq=%d", payload["seq"])
    except Exception as exc:
        logger.error("IoT Hub send failed: %s", exc)


async def _send_to_mqtt(host: str, port: int, username: str, password: str,
                        topic: str, payload: dict) -> None:
    try:
        import asyncio_mqtt as aiomqtt
        connect_kwargs: dict[str, Any] = {"hostname": host, "port": port}
        if username:
            connect_kwargs["username"] = username
            connect_kwargs["password"] = password
        async with aiomqtt.Client(**connect_kwargs) as client:
            await client.publish(topic, json.dumps(payload).encode(), qos=1)
        logger.debug("MQTT/Hono: sent seq=%d to %s", payload["seq"], topic)
    except Exception as exc:
        logger.error("MQTT send failed: %s", exc)


async def run_dual_mode() -> None:
    """Send every message to both IoT Hub and Hono simultaneously."""
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

    iothub_cs = os.environ.get("IOTHUB_DEVICE_CONNECTION_STRING", "")
    mqtt_host = os.environ.get("MQTT_HOST", "localhost")
    mqtt_port = int(os.environ.get("MQTT_PORT", "1883"))
    mqtt_user = os.environ.get("MQTT_USERNAME", "")
    mqtt_pass = os.environ.get("MQTT_PASSWORD", "")
    device_id = os.environ.get("DEVICE_ID", "dual-device-001")
    tenant_id = os.environ.get("TENANT_ID", "DEFAULT_TENANT")
    building_id = os.environ.get("BUILDING_ID", "ENG2")
    point_id = os.environ.get("POINT_ID", "dual-point-001")
    interval = float(os.environ.get("TELEMETRY_INTERVAL", "10"))
    mqtt_topic = f"telemetry/{tenant_id}/{device_id}"

    if not iothub_cs:
        logger.warning("IOTHUB_DEVICE_CONNECTION_STRING not set — IoT Hub leg disabled")
    if not mqtt_host:
        logger.warning("MQTT_HOST not set — MQTT/Hono leg disabled")

    seq = 0
    logger.info("Dual mode started: interval=%.1fs", interval)
    while True:
        payload = _build_telemetry(seq, device_id, building_id, point_id)
        tasks = []
        if iothub_cs:
            tasks.append(_send_to_iothub(iothub_cs, payload))
        if mqtt_host:
            tasks.append(_send_to_mqtt(mqtt_host, mqtt_port, mqtt_user, mqtt_pass, mqtt_topic, payload))

        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
            logger.info("Dual-sent seq=%d (iothub=%s mqtt=%s)", seq, bool(iothub_cs), bool(mqtt_host))
        seq += 1
        await asyncio.sleep(interval)


if __name__ == "__main__":
    asyncio.run(run_dual_mode())
