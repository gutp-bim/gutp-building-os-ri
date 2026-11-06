#!/usr/bin/env python3
"""NATS-only ceiling, no ConnectorWorker. Mirrors what NatsIngressTelemetryBus does per frame:
JetStream publish (awaiting ack) + KV put (history=1). Reports ops/s and per-op latency."""
import asyncio, json, sys, time
import nats
from nats.js.api import StreamConfig, KeyValueConfig, StorageType

DUR = float(sys.argv[1]) if len(sys.argv) > 1 else 8
URL = "nats://localhost:4222"
PAYLOAD = json.dumps({"telemetries": [{"point_id": "x", "building": "b", "device_id": "d", "name": "n",
    "datetime": "2026-11-06T00:00:00+00:00", "value": 21.5, "data": {}}]}).encode()


def pct(v, p):
    v = sorted(v); return v[min(len(v) - 1, int(p * (len(v) - 1)))] * 1000 if v else 0


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


async def run_mode(js, kv, mode, n):
    lat, counts = [], []
    stop_at = time.perf_counter() + DUR
    await asyncio.gather(*[worker(js, kv, mode, i, stop_at, lat, counts) for i in range(n)])
    total = sum(counts)
    return {"mode": mode, "tasks": n, "ops_per_s": round(total / DUR), "p50_ms": round(pct(lat, .5), 2),
            "p99_ms": round(pct(lat, .99), 2)}


async def pipelined(js, window):
    sent = 0
    stop_at = time.perf_counter() + DUR
    pending = []
    while time.perf_counter() < stop_at:
        pending.append(asyncio.ensure_future(js.publish("bench.telemetry", PAYLOAD)))
        sent += 1
        if len(pending) >= window:
            await asyncio.gather(*pending); pending = []
    await asyncio.gather(*pending)
    return {"mode": f"pub-pipelined(window={window})", "tasks": 1, "ops_per_s": round(sent / DUR)}


async def main():
    nc = await nats.connect(URL)
    js = nc.jetstream()
    try:
        await js.delete_stream("BENCH")
    except Exception:
        pass
    await js.add_stream(StreamConfig(name="BENCH", subjects=["bench.>"], storage=StorageType.FILE,
                                     max_age=300, max_bytes=512 * 1024 * 1024))
    try:
        await js.delete_key_value("benchkv")
    except Exception:
        pass
    kv = await js.create_key_value(KeyValueConfig(bucket="benchkv", history=1))
    out = []
    for mode, n in [("pub", 1), ("pub+kv", 1), ("pub", 3), ("pub+kv", 3), ("pub", 6), ("pub+kv", 6),
                    ("pub", 12), ("pub+kv", 12), ("pub", 24), ("pub+kv", 24)]:
        r = await run_mode(js, kv, mode, n); print(json.dumps(r), flush=True); out.append(r)
    for w in (50, 200):
        r = await pipelined(js, w); print(json.dumps(r), flush=True); out.append(r)
    await js.delete_stream("BENCH"); await js.delete_key_value("benchkv")
    await nc.close()

asyncio.run(main())
