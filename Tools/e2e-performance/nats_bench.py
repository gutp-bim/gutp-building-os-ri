#!/usr/bin/env python3
"""NATS-only ceiling, no ConnectorWorker (#625 / #626 baseline tool).

Mirrors what NatsIngressTelemetryBus does per frame — a JetStream publish awaiting its ack, and
(in ``pub+kv`` mode) a KV put (history=1) — and reports ops/s plus per-op latency for 1..N
concurrent sequential loops, then a pipelined publish (acks awaited per window) for the server's
raw publish ceiling. Run it on the *same* NATS the sweep targets, at the same time as an S22 step
when you need a same-moment reference for publish-ack / KV-put p50/p99 (PRD §5.3).

Usage:
  python nats_bench.py [--url nats://localhost:4222] [--duration 8] [--tasks 1,3,6,12,24]
                       [--windows 50,200] [--modes pub,pub+kv] [--json out.json]
"""
from __future__ import annotations

import argparse
import asyncio
import json
import time

import nats
from nats.js.api import KeyValueConfig, StorageType, StreamConfig

PAYLOAD = json.dumps({"telemetries": [{"point_id": "x", "building": "b", "device_id": "d", "name": "n",
    "datetime": "2026-11-06T00:00:00+00:00", "value": 21.5, "data": {}}]}).encode()


def pct(v: list[float], p: float) -> float:
    v = sorted(v)
    return v[min(len(v) - 1, int(p * (len(v) - 1)))] * 1000 if v else 0


async def worker(js, kv, mode, idx, stop_at, lat, counts):
    i = 0
    while time.perf_counter() < stop_at:
        t = time.perf_counter()
        await js.publish("bench.telemetry", PAYLOAD)
        if mode == "pub+kv":
            await kv.put(f"p{idx}-{i % 100}", PAYLOAD)
        lat.append(time.perf_counter() - t)
        i += 1
    counts.append(i)


async def run_mode(js, kv, mode, n, duration):
    lat, counts = [], []
    stop_at = time.perf_counter() + duration
    await asyncio.gather(*[worker(js, kv, mode, i, stop_at, lat, counts) for i in range(n)])
    total = sum(counts)
    return {"mode": mode, "tasks": n, "ops_per_s": round(total / duration), "p50_ms": round(pct(lat, .5), 2),
            "p99_ms": round(pct(lat, .99), 2)}


async def pipelined(js, window, duration):
    sent = 0
    stop_at = time.perf_counter() + duration
    pending = []
    while time.perf_counter() < stop_at:
        pending.append(asyncio.ensure_future(js.publish("bench.telemetry", PAYLOAD)))
        sent += 1
        if len(pending) >= window:
            await asyncio.gather(*pending)
            pending = []
    await asyncio.gather(*pending)
    return {"mode": f"pub-pipelined(window={window})", "tasks": 1, "ops_per_s": round(sent / duration)}


async def main_async(args) -> list[dict]:
    nc = await nats.connect(args.url)
    js = nc.jetstream()
    try:
        await js.delete_stream("BENCH")
    except Exception:  # noqa: BLE001
        pass
    await js.add_stream(StreamConfig(name="BENCH", subjects=["bench.>"], storage=StorageType.FILE,
                                     max_age=300, max_bytes=512 * 1024 * 1024))
    try:
        await js.delete_key_value("benchkv")
    except Exception:  # noqa: BLE001
        pass
    kv = await js.create_key_value(KeyValueConfig(bucket="benchkv", history=1))
    out = []
    try:
        for n in [int(x) for x in args.tasks.split(",") if x.strip()]:
            for mode in [m.strip() for m in args.modes.split(",") if m.strip()]:
                r = await run_mode(js, kv, mode, n, args.duration)
                print(json.dumps(r), flush=True)
                out.append(r)
        for w in [int(x) for x in args.windows.split(",") if x.strip()]:
            r = await pipelined(js, w, args.duration)
            print(json.dumps(r), flush=True)
            out.append(r)
    finally:
        await js.delete_stream("BENCH")
        await js.delete_key_value("benchkv")
        await nc.close()
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description="NATS-only publish-ack / KV-put ceiling")
    ap.add_argument("--url", default="nats://localhost:4222")
    ap.add_argument("--duration", type=float, default=8.0, help="seconds per mode")
    ap.add_argument("--tasks", default="1,3,6,12,24", help="concurrent sequential loops to try")
    ap.add_argument("--modes", default="pub,pub+kv")
    ap.add_argument("--windows", default="50,200", help="pipelined publish windows to try")
    ap.add_argument("--json", default=None, help="write all results to this file")
    args = ap.parse_args()
    out = asyncio.run(main_async(args))
    if args.json:
        with open(args.json, "w") as f:
            json.dump({"url": args.url, "duration_s": args.duration, "results": out}, f, indent=2)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
