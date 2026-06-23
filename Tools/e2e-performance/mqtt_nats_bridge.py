#!/usr/bin/env python3
"""MQTT → NATS JetStream bridge.

Subscribes to Mosquitto MQTT topics `building-os.raw.*` and forwards each
message to the corresponding NATS subject with the same name.  This replaces
the MQTT-forwarding portion of e2e_pipeline_bridge.py.

The ConnectorWorker (building-os.connector-worker in docker-compose) then
processes NATS raw messages and publishes to building-os.validated.telemetry.

Run:
    python mqtt_nats_bridge.py
    # or: docker compose -f docker-compose.oss.yaml up -d building-os.mqtt-nats-bridge
"""
from __future__ import annotations

import asyncio
import logging
import os
import signal

import aiomqtt
import nats
import nats.js
import nats.js.errors
from nats.js.api import StreamConfig

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)

MQTT_HOST = os.environ.get("MQTT_HOST", "localhost")
MQTT_PORT = int(os.environ.get("MQTT_PORT", "1883"))
NATS_URL = os.environ.get("NATS_URL", "nats://localhost:4222")

DEVICE_TYPES = ["hvac", "bacnet", "environmental", "electric", "behavior"]
RAW_TOPICS = [f"building-os.raw.{t}" for t in DEVICE_TYPES]
_STREAM_RAW = "BUILDING_OS_RAW"

_STREAM_VALIDATED = "BUILDING_OS_VALIDATED"
_SUBJECT_VALIDATED = "building-os.validated.telemetry"


async def _ensure_stream(js: nats.js.JetStreamContext, name: str, subjects: list[str]) -> None:
    try:
        await js.add_stream(StreamConfig(name=name, subjects=subjects))
        logger.info("Created NATS stream %s", name)
    except nats.js.errors.BadRequestError:
        logger.info("NATS stream %s already exists", name)


async def run() -> None:
    nc = await nats.connect(NATS_URL)
    js = nc.jetstream()
    await _ensure_stream(js, _STREAM_RAW, RAW_TOPICS)
    await _ensure_stream(js, _STREAM_VALIDATED, [_SUBJECT_VALIDATED])

    stop_event = asyncio.Event()

    def _signal_handler(*_):
        logger.info("Received shutdown signal")
        stop_event.set()

    loop = asyncio.get_running_loop()
    for sig in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(sig, _signal_handler)

    all_topics = RAW_TOPICS + [_SUBJECT_VALIDATED]
    forwarded = 0
    async with aiomqtt.Client(hostname=MQTT_HOST, port=MQTT_PORT) as mqtt:
        for topic in all_topics:
            await mqtt.subscribe(topic, qos=1)
        logger.info("Subscribed to MQTT %s on %s:%d", all_topics, MQTT_HOST, MQTT_PORT)

        async for message in mqtt.messages:
            if stop_event.is_set():
                break
            topic = str(message.topic)
            if not (topic.startswith("building-os.raw.") or topic == _SUBJECT_VALIDATED):
                continue
            try:
                await js.publish(topic, message.payload)
                forwarded += 1
                if forwarded % 100 == 0:
                    logger.info("Forwarded %d messages", forwarded)
            except Exception as exc:  # noqa: BLE001
                logger.warning("Forward error (topic=%s): %s", topic, exc)

    logger.info("Shutting down. Total forwarded: %d", forwarded)
    await nc.drain()


if __name__ == "__main__":
    asyncio.run(run())
