#!/usr/bin/env python3
"""NATS validated telemetry → TimescaleDB consumer (ConsumerWorker).

Subscribes to the NATS JetStream `building-os.validated.telemetry` subject,
parses each message as ValidTelemetryData JSON, and inserts batches into
the TimescaleDB `telemetry` table.

This replaces the DB-write portion of e2e_pipeline_bridge.py.  The
mqtt_nats_bridge + ConnectorWorker + this service together replace the
monolithic bridge:

    Mosquitto → mqtt_nats_bridge → NATS raw
                                         → ConnectorWorker → NATS validated
                                                                  → telemetry_consumer → TimescaleDB

Run:
    python telemetry_consumer.py
    # or: docker compose -f docker-compose.oss.yaml up -d building-os.telemetry-consumer
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import signal
from datetime import datetime, timezone

import psycopg2
import psycopg2.extras
import nats
import nats.js
import nats.js.errors
from nats.js.api import StreamConfig, ConsumerConfig, AckPolicy, DeliverPolicy

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)

NATS_URL = os.environ.get("NATS_URL", "nats://localhost:4222")
TIMESCALE_DSN = os.environ.get(
    "TIMESCALE_DSN",
    "postgresql://buildingos:buildingos@localhost:5433/buildingos",
)
BATCH_SIZE = int(os.environ.get("CONSUMER_BATCH_SIZE", "50"))
FLUSH_INTERVAL_S = float(os.environ.get("CONSUMER_FLUSH_INTERVAL", "2.0"))

_STREAM_VALIDATED = "BUILDING_OS_VALIDATED"
_SUBJECT_VALIDATED = "building-os.validated.telemetry"
_CONSUMER_NAME = "telemetry-consumer-worker"


async def _ensure_stream(js: nats.js.JetStreamContext) -> None:
    try:
        await js.add_stream(StreamConfig(name=_STREAM_VALIDATED, subjects=[_SUBJECT_VALIDATED]))
        logger.info("Created NATS stream %s", _STREAM_VALIDATED)
    except nats.js.errors.BadRequestError:
        logger.info("NATS stream %s already exists", _STREAM_VALIDATED)


def _parse_ts(raw: str | None) -> datetime:
    if not raw:
        return datetime.now(timezone.utc)
    try:
        return datetime.fromisoformat(raw).astimezone(timezone.utc)
    except (ValueError, TypeError):
        return datetime.now(timezone.utc)


def _extract_rows(data: object) -> list[dict]:
    """Handle { "telemetries": [...] } wrapper (snake_case) or bare list/dict."""
    if isinstance(data, dict):
        items = data.get("telemetries")
        if isinstance(items, list):
            return items
        return [data]
    if isinstance(data, list):
        return data
    return []


def _insert_batch(conn, rows: list[dict]) -> int:
    if not rows:
        return 0
    with conn.cursor() as cur:
        psycopg2.extras.execute_values(
            cur,
            """
            INSERT INTO telemetry (time, point_id, building, device_id, name, value, data, id)
            VALUES %s
            ON CONFLICT DO NOTHING
            """,
            [
                (
                    _parse_ts(r.get("datetime")),
                    r.get("point_id") or "",
                    r.get("building"),
                    r.get("device_id") or "",
                    r.get("name"),
                    r.get("value"),
                    json.dumps(r.get("data") or {}),
                    r.get("id"),
                )
                for r in rows
            ],
            template=None,
            page_size=100,
        )
    conn.commit()
    return len(rows)


async def run() -> None:
    nc = await nats.connect(NATS_URL)
    js = nc.jetstream()
    await _ensure_stream(js)

    stop_event = asyncio.Event()

    def _signal_handler(*_):
        logger.info("Received shutdown signal")
        stop_event.set()

    loop = asyncio.get_running_loop()
    for sig in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(sig, _signal_handler)

    psql = psycopg2.connect(TIMESCALE_DSN)
    total_written = 0
    # Buffer rows paired with their NATS messages for deferred ack after DB insert
    pending_rows: list[dict] = []
    pending_msgs: list = []
    last_flush = asyncio.get_event_loop().time()

    try:
        sub = await js.subscribe(
            _SUBJECT_VALIDATED,
            durable=_CONSUMER_NAME,
            manual_ack=True,
            deliver_policy=DeliverPolicy.LAST,
        )
        logger.info("Subscribed to NATS %s (durable=%s)", _SUBJECT_VALIDATED, _CONSUMER_NAME)

        while not stop_event.is_set():
            try:
                msg = await asyncio.wait_for(sub.next_msg(), timeout=FLUSH_INTERVAL_S)
                rows = _extract_rows(json.loads(msg.data))
                pending_rows.extend(rows)
                pending_msgs.append(msg)
            except asyncio.TimeoutError:
                pass
            except Exception as exc:  # noqa: BLE001
                logger.warning("Consumer error: %s", exc)

            now = asyncio.get_event_loop().time()
            if len(pending_rows) >= BATCH_SIZE or (pending_rows and now - last_flush >= FLUSH_INTERVAL_S):
                written = _insert_batch(psql, pending_rows)
                total_written += written
                if written:
                    logger.info("Wrote %d rows (total=%d)", written, total_written)
                # Ack only after successful DB insert
                for m in pending_msgs:
                    try:
                        await m.ack()
                    except Exception:  # noqa: BLE001
                        pass
                pending_rows.clear()
                pending_msgs.clear()
                last_flush = now

        # Final flush
        if pending_rows:
            _insert_batch(psql, pending_rows)
            for m in pending_msgs:
                try:
                    await m.ack()
                except Exception:  # noqa: BLE001
                    pass

    finally:
        psql.close()
        await nc.drain()
    logger.info("Shutdown complete. Total rows written: %d", total_written)


if __name__ == "__main__":
    asyncio.run(run())
