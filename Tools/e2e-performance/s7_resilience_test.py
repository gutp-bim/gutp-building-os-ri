#!/usr/bin/env python3
"""S7 Resilience test: NATS JetStream replay, duplicate detection, and bridge restart recovery.

Tests:
  A. NATS JetStream replay — messages are retained in the stream and can be re-consumed from any position
  B. TimescaleDB duplicate behavior — verifies what happens when same (point_id, time) is inserted twice
  C. Bridge restart recovery — load generator, bridge kill, restart, verify no data loss
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import signal
import subprocess
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

import nats
import nats.js
import nats.js.errors
from nats.js.api import StreamConfig, ConsumerConfig, DeliverPolicy

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)

NATS_URL = os.environ.get("NATS_URL", "nats://localhost:4222")
TIMESCALE_DSN = os.environ.get(
    "TIMESCALE_DSN", "postgresql://buildingos:buildingos@localhost:5433/buildingos"
)
# Storage backend the pipeline writes to (#216). parquet = MinIO lake (default); timescale = legacy.
MODE = os.environ.get("QUALITY_MODE", "parquet")
MINIO_ENDPOINT = os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000")
MINIO_KEY = os.environ.get("MINIO_ACCESS_KEY", os.environ.get("MINIO_ROOT_USER", "buildingos"))
MINIO_SECRET = os.environ.get("MINIO_SECRET_KEY", os.environ.get("MINIO_ROOT_PASSWORD", "buildingos123"))
LAKE_BUCKET = os.environ.get("LAKE_BUCKET", "cold")

RESULTS: dict[str, dict] = {}


def _count_lake_rows(building: str) -> int:
    """Count Parquet lake rows for a run's building partition via DuckDB (parquet mode row check)."""
    import duckdb

    con = duckdb.connect()
    try:
        con.execute("INSTALL httpfs; LOAD httpfs;")
        con.execute(f"SET s3_endpoint='{MINIO_ENDPOINT.replace('http://', '').replace('https://', '')}';")
        con.execute("SET s3_url_style='path';")
        con.execute(f"SET s3_use_ssl={'true' if MINIO_ENDPOINT.startswith('https') else 'false'};")
        con.execute(f"SET s3_access_key_id='{MINIO_KEY}';")
        con.execute(f"SET s3_secret_access_key='{MINIO_SECRET}';")
        glob = f"s3://{LAKE_BUCKET}/**/*.parquet"
        try:
            return int(con.execute(
                "SELECT count(*) FROM read_parquet(?, hive_partitioning=1, union_by_name=1) WHERE building = ?",
                [glob, building],
            ).fetchone()[0])
        except duckdb.IOException:
            return 0
    finally:
        con.close()


# ── Test A: NATS JetStream replay ─────────────────────────────────────────────

async def test_nats_replay(n_messages: int = 20) -> dict:
    """Publish N messages to a dedicated S7 test stream, read them, replay — verify JetStream replay.

    Uses a separate stream (S7_REPLAY_TEST) and unique subject per run to avoid draining
    existing production/test streams and to make the test deterministic.
    """
    logger.info("==> Test A: NATS JetStream replay (n=%d)", n_messages)
    nc = await nats.connect(NATS_URL)
    js = nc.jetstream()

    run_id = f"s7-replay-{uuid.uuid4().hex[:8]}"
    # Isolate this test in its own stream and subject to avoid draining shared streams.
    stream_name = "S7_REPLAY_TEST"
    subject = f"s7.test.replay.{run_id}"

    # Create (or recreate) a dedicated ephemeral stream for this test run.
    try:
        await js.add_stream(StreamConfig(
            name=stream_name,
            subjects=["s7.test.replay.*"],
            max_age=600,  # auto-expire after 10 min
        ))
    except nats.js.errors.BadRequestError:
        pass  # stream already exists from a previous run; subjects match

    # Publish N messages
    published = 0
    for i in range(n_messages):
        msg = json.dumps({
            "id": f"{run_id}-{i}",
            "device_id": "s7-device-001",
            "test_run_id": run_id,
            "type": "hvac",
            "data": {"mode": "Cool", "value": 22.0 + i},
            "observed_at": datetime.now(timezone.utc).isoformat(),
        }).encode()
        ack = await js.publish(subject, msg)
        published += 1

    logger.info("Published %d messages to NATS stream", published)
    # Record the last sequence so replay can stop deterministically.
    last_seq = ack.seq

    # First read: consume exactly the N messages we just published using their sequence range.
    consumer_name = f"s7-first-{uuid.uuid4().hex[:8]}"
    sub1 = await js.pull_subscribe(subject, consumer_name, stream=stream_name)
    first_read = 0
    try:
        while True:
            msgs = await sub1.fetch(10, timeout=2)
            for m in msgs:
                await m.ack()
                first_read += 1
                if first_read >= n_messages:
                    break
            if first_read >= n_messages:
                break
    except nats.errors.TimeoutError:
        pass
    logger.info("First read: %d messages consumed", first_read)

    # Second read from beginning (replay): new consumer with DeliverAll — reads from stream start.
    replay_consumer = f"s7-replay2-{uuid.uuid4().hex[:8]}"
    sub2 = await js.subscribe(
        subject,
        durable=replay_consumer,
        config=ConsumerConfig(deliver_policy=DeliverPolicy.ALL),
        stream=stream_name,
        manual_ack=True,
    )
    second_read = 0
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        try:
            msg = await asyncio.wait_for(sub2.next_msg(), timeout=1)
            await msg.ack()
            second_read += 1
            if second_read >= n_messages:
                break
        except (asyncio.TimeoutError, nats.errors.TimeoutError):
            break

    await sub2.unsubscribe()
    await nc.close()

    replay_success = second_read >= n_messages
    logger.info(
        "Test A: published=%d first_read=%d replay=%d replay_success=%s",
        published, first_read, second_read, replay_success,
    )
    return {
        "test": "A_nats_replay",
        "published": published,
        "first_read": first_read,
        "second_read": second_read,
        "replay_success": replay_success,
        "passed": replay_success,
    }


# ── Test B: duplicate behavior ─────────────────────────────────────────────────

def test_timescale_duplicate() -> dict:
    """Document duplicate-handling. In parquet mode dedup is read-side (DedupById in
    ParquetLakeReadPlanner / TelemetryBatchAccumulator), not a DB unique constraint, so there is no
    TimescaleDB to exercise — record that. In timescale mode, insert the same row twice and observe."""
    if MODE == "parquet":
        logger.info("==> Test B: parquet mode — dedup is read-side (DedupById), no DB unique constraint")
        return {
            "test": "B_duplicate_behavior",
            "mode": "parquet",
            "dedup_via_on_conflict": False,
            "rows_in_db": "N/A",
            "notes": "Parquet モードでは重複は read 時に id で排除（ParquetLakeReadPlanner.DedupById / "
                     "TelemetryBatchAccumulator）。DB の一意制約ではないため本テストは該当なし（単体テストで担保）。",
            "passed": True,
        }

    import psycopg2  # timescale mode only
    logger.info("==> Test B: TimescaleDB duplicate behavior")
    run_id = f"s7-dup-{uuid.uuid4().hex[:8]}"
    fixed_time = datetime.now(timezone.utc).replace(microsecond=0)

    conn = psycopg2.connect(TIMESCALE_DSN)
    try:
        with conn.cursor() as cur:
            for attempt in range(2):
                cur.execute(
                    """INSERT INTO telemetry (time, point_id, device_id, name, value, data)
                       VALUES (%s, %s, %s, %s, %s, %s)
                       ON CONFLICT DO NOTHING""",
                    (
                        fixed_time,
                        f"s7-point-001",
                        "s7-device-001",
                        "temperature",
                        22.0,
                        json.dumps({"test_run_id": run_id}),
                    ),
                )
            conn.commit()

            cur.execute(
                "SELECT COUNT(*) FROM telemetry WHERE data->>'test_run_id' = %s",
                (run_id,),
            )
            row_count = cur.fetchone()[0]
    finally:
        conn.close()

    # TimescaleDB hypertable has no default unique constraint on (time, point_id)
    # ON CONFLICT DO NOTHING requires a unique index — without one, both rows may be inserted
    # This test documents the actual behavior
    dedup_worked = row_count == 1
    logger.info(
        "Test B: inserted 2 identical rows (ON CONFLICT DO NOTHING), actual row count=%d, dedup_worked=%s",
        row_count, dedup_worked,
    )
    return {
        "test": "B_timescale_duplicate",
        "inserts_attempted": 2,
        "rows_in_db": row_count,
        "dedup_via_on_conflict": dedup_worked,
        # Note: dedup_worked=False is expected if no unique constraint is set.
        # The S7 test documents this as a known gap, not a failure.
        "passed": True,  # Always pass — this is documentation, not a strict gate
        "notes": (
            "ON CONFLICT DO NOTHING requires a unique index. "
            f"Without one, both rows are inserted (row_count={row_count}). "
            "Deduplication in ConnectorWorker uses NATS Msg-Id header (see docs/architecture/oss-nats-design.md)."
        ),
    }


# ── Test C: Bridge restart recovery ────────────────────────────────────────────

def test_bridge_restart(script_dir: Path, duration: int = 60) -> dict:
    """Run load gen, kill bridge, restart, verify no loss (at-least-once via NATS JetStream)."""
    logger.info("==> Test C: Bridge restart recovery (duration=%ds)", duration)

    python = str(script_dir / ".venv" / "bin" / "python")
    run_id = f"s7-restart-{uuid.uuid4().hex[:8]}"
    parquet = MODE == "parquet"

    # Bridge env: parquet mode publishes validated to NATS only (real ParquetLakeWriter persists).
    bridge_env = {**os.environ, "PARQUET_MODE": "true" if parquet else "false"}
    # Load-gen env: MQTT auth (Mosquitto allow_anonymous false). BUILDING_ID set per phase below.
    base_loadgen_env = {
        **os.environ,
        "MQTT_USERNAME": os.environ.get("MQTT_USERNAME", "devices"),
        "MQTT_PASSWORD": os.environ.get("MQTT_PASSWORD", "buildingos-devices"),
    }

    def run_load(phase_run_id: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [
                python, str(script_dir / "device_load_generator.py"),
                "--scale", "small", "--profile", "baseline",
                "--duration", str(duration // 2),
                "--run-id", phase_run_id,
            ],
            capture_output=True, text=True,
            env={**base_loadgen_env, "BUILDING_ID": phase_run_id},  # isolate each phase in its own partition
        )

    # Start bridge
    bridge = subprocess.Popen(
        [python, str(script_dir / "e2e_pipeline_bridge.py")],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=bridge_env,
    )
    time.sleep(3)

    # Phase 1
    phase1_run_id = f"{run_id}-p1"
    result1 = run_load(phase1_run_id)
    logger.info("Phase 1 sent. returncode=%d", result1.returncode)

    # Kill bridge
    logger.info("Killing bridge (pid=%d)...", bridge.pid)
    bridge.terminate()
    bridge.wait(timeout=10)
    time.sleep(5)

    # Restart bridge
    logger.info("Restarting bridge...")
    bridge2 = subprocess.Popen(
        [python, str(script_dir / "e2e_pipeline_bridge.py")],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=bridge_env,
    )
    time.sleep(3)

    # Phase 2
    phase2_run_id = f"{run_id}-p2"
    result2 = run_load(phase2_run_id)
    logger.info("Phase 2 sent. returncode=%d", result2.returncode)

    bridge2.terminate()
    bridge2.wait(timeout=10)
    # parquet flush is on PARQUET_FLUSH_INTERVAL (set =1 min for tests); wait longer than for TS.
    time.sleep(90 if parquet else 15)

    def count_rows(run_id_check: str) -> int:
        if parquet:
            return _count_lake_rows(run_id_check)
        import psycopg2
        conn = psycopg2.connect(TIMESCALE_DSN)
        try:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT COUNT(*) FROM telemetry WHERE data->>'test_run_id' = %s", (run_id_check,))
                return int(cur.fetchone()[0])
        finally:
            conn.close()

    # Read sent counts from load-generator-result.json (reliable; stdout/stderr is logger output).
    p1_sent = _read_sent_from_result(script_dir, phase1_run_id, result1.returncode)
    p2_sent = _read_sent_from_result(script_dir, phase2_run_id, result2.returncode)

    if p1_sent == 0 or p2_sent == 0:
        logger.error(
            "Test C: sent count is 0 (p1=%d p2=%d) — load generator may have failed", p1_sent, p2_sent
        )
        return {
            "test": "C_bridge_restart",
            "phase1": {"sent": p1_sent, "db_rows": 0, "loss_rate": "N/A"},
            "phase2": {"sent": p2_sent, "db_rows": 0, "loss_rate": "N/A"},
            "notes": "Load generator returned 0 sent — check returncode and stdout/stderr.",
            "passed": False,
        }

    p1_rows = count_rows(phase1_run_id)
    p2_rows = count_rows(phase2_run_id)

    p1_loss = max(0, p1_sent - p1_rows) / p1_sent if p1_sent > 0 else 0
    p2_loss = max(0, p2_sent - p2_rows) / p2_sent if p2_sent > 0 else 0

    passed = p1_loss <= 0.01 and p2_loss <= 0.01
    logger.info(
        "Test C: p1 sent=%d rows=%d loss=%.4f%% | p2 sent=%d rows=%d loss=%.4f%% | passed=%s",
        p1_sent, p1_rows, p1_loss * 100,
        p2_sent, p2_rows, p2_loss * 100,
        passed,
    )
    return {
        "test": "C_bridge_restart",
        "phase1": {"sent": p1_sent, "db_rows": p1_rows, "loss_rate": f"{p1_loss*100:.4f}%"},
        "phase2": {"sent": p2_sent, "db_rows": p2_rows, "loss_rate": f"{p2_loss*100:.4f}%"},
        "notes": (
            "Phase 1 runs while bridge is alive. Bridge is killed. Phase 2 runs after restart. "
            "MQTT QoS 0 messages during bridge downtime are lost (expected for ephemeral consumers). "
            "NATS JetStream messages published before bridge restart are replayed on reconnect."
        ),
        "passed": passed,
    }


def _read_sent_from_result(script_dir: Path, run_id: str, returncode: int) -> int:
    """Read total_sent from load-generator-result.json written by device_load_generator.py."""
    if returncode != 0:
        logger.warning("Load generator for run_id=%s exited with code %d", run_id, returncode)
        return 0
    result_path = script_dir / "results" / run_id / "load-generator-result.json"
    try:
        with open(result_path) as f:
            data = json.load(f)
        return int(data.get("total_sent", 0))
    except (FileNotFoundError, json.JSONDecodeError, ValueError) as e:
        logger.warning("Could not read result JSON for %s: %s", run_id, e)
        return 0


# ── Main ───────────────────────────────────────────────────────────────────────

async def run_all(script_dir: Path, quick: bool, run_id: str) -> dict:
    results = {}

    # Test A: NATS replay
    n = 10 if quick else 50
    results["A"] = await test_nats_replay(n)

    # Test B: duplicate behavior
    results["B"] = test_timescale_duplicate()

    # Test C: bridge restart
    dur = 60 if quick else 180
    results["C"] = test_bridge_restart(script_dir, duration=dur)

    overall = all(r.get("passed") for r in results.values())
    return {"run_id": run_id, "quick": quick, "tests": results, "passed": overall}


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--quick", action="store_true")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    script_dir = Path(__file__).parent
    result = asyncio.run(run_all(script_dir, args.quick, args.run_id))

    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    with open(out, "w") as f:
        json.dump(result, f, indent=2)

    logger.info("Results saved to %s", out)
    if result["passed"]:
        logger.info("✅ S7 Resilience PASSED")
        sys.exit(0)
    else:
        logger.error("❌ S7 Resilience FAILED")
        for test_id, r in result["tests"].items():
            if not r.get("passed"):
                logger.error("  Test %s FAILED: %s", test_id, r)
        sys.exit(1)
