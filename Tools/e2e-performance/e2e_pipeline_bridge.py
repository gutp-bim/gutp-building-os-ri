#!/usr/bin/env python3
"""E2E pipeline bridge for local testing: Mosquitto → NATS JetStream → TimescaleDB.

Replaces the ConnectorWorker + TelemetryWriter for test environments where those
services are not running.  The bridge:
  1. Creates required JetStream streams on startup.
  2. Subscribes to all building-os.raw.<type> topics on Mosquitto.
  3. For each received message: publishes to NATS raw stream, transforms to
     validated-telemetry format, publishes to NATS validated stream, and inserts
     into the TimescaleDB telemetry table.

Run in background before the load generator:
  python e2e_pipeline_bridge.py &
  BRIDGE_PID=$!
  ...
  kill $BRIDGE_PID
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import signal
from datetime import datetime, timezone

import aiomqtt
import nats
import nats.js
import nats.js.errors
import psycopg2
import psycopg2.extras
from nats.js.api import StreamConfig

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)

MQTT_HOST = os.environ.get("MQTT_HOST", "localhost")
MQTT_PORT = int(os.environ.get("MQTT_PORT", "1883"))
# Mosquitto in the OSS stack requires auth (allow_anonymous false). Empty → anonymous.
MQTT_USERNAME = os.environ.get("MQTT_USERNAME") or None
MQTT_PASSWORD = os.environ.get("MQTT_PASSWORD") or None
NATS_URL = os.environ.get("NATS_URL", "nats://localhost:4222")
TIMESCALE_DSN = os.environ.get(
    "TIMESCALE_DSN",
    "postgresql://buildingos:buildingos@localhost:5433/buildingos",
)

# PARQUET_MODE=true (default): publish raw+validated to NATS only and let the real
# ConnectorWorker ParquetLakeWriter (WARM_STORE=parquet) persist to the MinIO lake — no
# TimescaleDB write here. Set PARQUET_MODE=false for the legacy TimescaleDB direct-write path.
PARQUET_MODE = os.environ.get("PARQUET_MODE", "true").lower() != "false"

DEVICE_TYPES = ["hvac", "bacnet", "environmental", "electric", "behavior"]

_STREAM_RAW = "BUILDING_OS_RAW"
_STREAM_VALIDATED = "BUILDING_OS_VALIDATED"


async def _ensure_stream(js: nats.js.JetStreamContext, name: str, subjects: list[str]) -> None:
    try:
        await js.add_stream(StreamConfig(name=name, subjects=subjects))
        logger.info("Created NATS stream %s on %s", name, subjects)
    except nats.js.errors.BadRequestError:
        logger.info("NATS stream %s already exists", name)


async def _setup_streams(js: nats.js.JetStreamContext) -> None:
    await _ensure_stream(js, _STREAM_RAW, [f"building-os.raw.{t}" for t in DEVICE_TYPES])
    await _ensure_stream(js, _STREAM_VALIDATED, ["building-os.validated.telemetry"])


def _to_validated(raw: dict) -> dict:
    """Wrap a load-generator payload into the validated-telemetry envelope.

    test_run_id is merged into the data JSONB so quality_checker.py can filter
    rows by data->>'test_run_id'.
    """
    data = dict(raw.get("data") or {})
    if run_id := raw.get("test_run_id"):
        data["test_run_id"] = run_id

    return {
        "telemetries": [
            {
                "id": raw.get("id", ""),
                "device_id": raw.get("device_id", ""),
                "point_id": raw.get("point_id", ""),
                "building": raw.get("building", ""),
                "value": raw.get("value", 0.0),
                "data": data,
                "datetime": raw.get(
                    "datetime",
                    datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
                ),
                "name": raw.get("name", ""),
                "test_run_id": raw.get("test_run_id", ""),
            }
        ]
    }


def _write_rows(conn: psycopg2.extensions.connection, telemetries: list[dict]) -> int:
    written = 0
    with conn.cursor() as cur:
        for t in telemetries:
            try:
                cur.execute(
                    """
                    INSERT INTO telemetry
                        (time, point_id, building, device_id, name, value, data, id)
                    VALUES (%s, %s, %s, %s, %s, %s, %s::jsonb, %s)
                    ON CONFLICT DO NOTHING
                    """,
                    (
                        t.get("datetime"),
                        t.get("point_id", ""),
                        t.get("building"),
                        t.get("device_id", ""),
                        t.get("name"),
                        t.get("value"),
                        json.dumps(t.get("data") or {}),
                        t.get("id"),
                    ),
                )
                written += 1
            except Exception as exc:
                logger.warning("DB insert error: %s", exc)
                conn.rollback()
    conn.commit()
    return written


async def run_bridge(stop_event: asyncio.Event) -> dict:
    nc = await nats.connect(NATS_URL)
    js = nc.jetstream()
    await _setup_streams(js)

    db = None
    if PARQUET_MODE:
        logger.info(
            "PARQUET_MODE: publishing validated telemetry to NATS only; the real ParquetLakeWriter "
            "(WARM_STORE=parquet) persists to the MinIO lake. No TimescaleDB write.")
    else:
        db = psycopg2.connect(TIMESCALE_DSN)
        logger.info("Connected to TimescaleDB at %s", TIMESCALE_DSN)

    stats: dict[str, int] = {"received": 0, "nats_raw": 0, "nats_validated": 0, "db_rows": 0, "errors": 0}

    try:
        async with aiomqtt.Client(
            hostname=MQTT_HOST, port=MQTT_PORT, username=MQTT_USERNAME, password=MQTT_PASSWORD
        ) as mqtt:
            for dtype in DEVICE_TYPES:
                await mqtt.subscribe(f"building-os.raw.{dtype}", qos=1)
            logger.info("Subscribed to Mosquitto building-os.raw.* (host=%s port=%d)", MQTT_HOST, MQTT_PORT)

            try:
                async for message in mqtt.messages:
                    if stop_event.is_set():
                        break

                    topic = str(message.topic)
                    if not topic.startswith("building-os.raw."):
                        continue

                    try:
                        raw = json.loads(message.payload.decode())
                        stats["received"] += 1

                        await js.publish(topic, message.payload)
                        stats["nats_raw"] += 1

                        validated = _to_validated(raw)
                        await js.publish(
                            "building-os.validated.telemetry",
                            json.dumps(validated).encode(),
                        )
                        stats["nats_validated"] += 1

                        if db is not None:
                            written = _write_rows(db, validated["telemetries"])
                            stats["db_rows"] += written

                        if stats["received"] % 10 == 0:
                            logger.info(
                                "Bridge: received=%d nats_raw=%d nats_validated=%d db_rows=%d errors=%d",
                                stats["received"],
                                stats["nats_raw"],
                                stats["nats_validated"],
                                stats["db_rows"],
                                stats["errors"],
                            )

                    except asyncio.CancelledError:
                        raise
                    except Exception as exc:
                        logger.warning("Bridge error (topic=%s): %s", topic, exc)
                        stats["errors"] += 1
            except asyncio.CancelledError:
                pass  # clean shutdown on SIGTERM
    finally:
        logger.info(
            "Bridge stopped. received=%d nats_raw=%d nats_validated=%d db_rows=%d errors=%d",
            stats["received"],
            stats["nats_raw"],
            stats["nats_validated"],
            stats["db_rows"],
            stats["errors"],
        )
        if db is not None:
            db.close()
        await nc.drain()

    return stats


def main() -> None:
    loop = asyncio.new_event_loop()

    async def _run() -> None:
        stop_event = asyncio.Event()
        task = asyncio.create_task(run_bridge(stop_event))

        def _cancel(*_: object) -> None:
            stop_event.set()
            task.cancel()

        for sig in (signal.SIGINT, signal.SIGTERM):
            loop.add_signal_handler(sig, _cancel)

        try:
            await task
        except asyncio.CancelledError:
            pass

    try:
        loop.run_until_complete(_run())
    finally:
        loop.close()


if __name__ == "__main__":
    main()
