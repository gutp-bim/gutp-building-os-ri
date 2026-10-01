"""Demo telemetry feeder (#155, #458).

Streams realistic telemetry over the gRPC GatewayIngress so `make demo` shows live data with no
external gateway. Two datasets, both on by default in `make demo`:

1. The e2e control-demo points (`GW-SOS-001` / `SOS-PT-001..008`, from `fixtures/e2e/twin.ttl`),
   every `INTERVAL_SECONDS` — always healthy, so the control demo and `make demo-e2e` keep working.
2. The generated demo building (#458, `fixtures/demo/feeder-plan.json`, when `DEMO_PLAN_PATH` is
   set): ~1,200 points, each on its own expected interval, with the scenario from
   `DEMO_SCENARIO` (`degraded` by default, or `healthy`):
     - stale   — ONE reading back-dated to now − interval × 4 at startup, then silence, so the point
                 is past the interval × 3 stale threshold from the first second (deterministic,
                 instead of waiting interval × 3 for it to happen)
     - missing — never sent
     - alarm   — sent on its interval, at a value past alarmHigh
     - points behind a `disconnectedGateways` gateway are never sent, and that gateway opens no
       egress stream, so it reads as disconnected (ADR-0004) and its points as missing with reason
       gateway_disconnected
   `healthy` sends every point on its interval and connects every gateway.

When `EGRESS_TARGET` is set, the feeder also holds one GatewayEgress stream per connected gateway
(the GatewayBridge heartbeat is what makes a gateway "connected"), and answers any ControlCommand
it is sent with a successful ControlResult.

Design: value/plan/scheduling helpers are PURE and import-safe without grpcio (unit-tested in
`test_grpc_demo_feeder.py`); `grpc` / `grpc_tools` are imported lazily inside the send path. The
frame contract mirrors the E5 harness: a `TelemetryFrame` carries `gateway_id` + `point_id` +
`value` + `timestamp` only; BuildingOS enriches the static metadata from the twin (#181).
"""

from __future__ import annotations

import json
import math
import os
import threading
import time
import zlib
from dataclasses import dataclass

DEFAULT_GATEWAY_ID = "GW-SOS-001"
DEFAULT_INGRESS_TARGET = "building-os.connector-worker:5051"
DEFAULT_PROTO_PATH = "/proto/gateway_ingress.proto"
SCENARIOS = ("degraded", "healthy")
# stale = one reading this many intervals in the past (the stale threshold is interval × 3, #183)
STALE_BACKDATE_INTERVALS = 4
# ticks of an un-accepted initial burst before logging the 'twin lacks these points' hint
UNKNOWN_POINTS_HINT_AFTER_TICKS = 12


@dataclass(frozen=True)
class PointSpec:
    """A demo point and how to synthesize a plausible reading for it.

    `kind` is one of "number" / "boolean" / "enum". Booleans and enums are emitted
    as numeric codes because the ingress contract is numeric (#189). `lo`/`hi`
    bound numbers (clamp) and define the enum code range; 0/0 means unbounded.
    `period_s` 0 means a constant reading (`base`, off, or the lowest code).
    `phase_s` shifts the sine so points sharing a template do not move in lockstep.
    """

    point_id: str
    kind: str
    base: float
    amplitude: float
    period_s: float
    lo: float = 0.0
    hi: float = 0.0
    phase_s: float = 0.0


@dataclass(frozen=True)
class FeedPoint:
    """A point the feeder streams: its value model, owner gateway, cadence and scenario role."""

    spec: PointSpec
    gateway_id: str
    interval_s: float
    role: str = "normal"  # normal | stale | missing | alarm
    alarm_value: float | None = None


# Mirrors fixtures/e2e/twin.ttl (all under GW-SOS-001). Read-only points are
# 001/002/003/005/008; writable (control-demo) points are 004/006/007.
DEMO_POINTS: tuple[PointSpec, ...] = (
    PointSpec("SOS-PT-001", "number", 24.0, 2.0, 300.0),                 # Room Temperature degC
    PointSpec("SOS-PT-002", "number", 50.0, 8.0, 420.0),                 # Room Humidity %
    PointSpec("SOS-PT-003", "number", 600.0, 180.0, 600.0),              # CO2 ppm
    PointSpec("SOS-PT-004", "boolean", 0.0, 0.0, 900.0),                 # Lighting On/Off
    PointSpec("SOS-PT-005", "boolean", 0.0, 0.0, 240.0),                 # Occupancy
    PointSpec("SOS-PT-006", "number", 23.0, 1.0, 1800.0, lo=16.0, hi=30.0),  # Setpoint degC (16-30)
    PointSpec("SOS-PT-007", "enum", 0.0, 0.0, 480.0, lo=0.0, hi=3.0),    # Fan Speed 0..3
    PointSpec("SOS-PT-008", "number", 1.8, 0.9, 360.0),                  # Active Power kW
)


def demo_value(spec: PointSpec, elapsed_s: float) -> float:
    """Deterministic, realistic-looking reading for `spec` at `elapsed_s` seconds.

    Pure (a sine over the point's period, no RNG) so it is reproducible and
    unit-testable. Returns a numeric code for boolean/enum points.
    """
    if spec.period_s <= 0:
        # constant: base for numbers; off / the lowest code for boolean / enum
        phase = 0.0 if spec.kind == "number" else -1.0
    else:
        t = (elapsed_s + spec.phase_s) % spec.period_s
        phase = math.sin(2.0 * math.pi * t / spec.period_s)
    if spec.kind == "number":
        value = spec.base + spec.amplitude * phase
        if spec.hi > spec.lo:  # clamp bounded points (e.g. setpoint 16-30)
            value = max(spec.lo, min(spec.hi, value))
        return round(value, 2)
    if spec.kind == "boolean":
        return 1.0 if phase >= 0.0 else 0.0
    if spec.kind == "enum":
        steps = int(spec.hi - spec.lo) + 1
        idx = int(((phase + 1.0) / 2.0) * steps)  # map [-1,1] → [0, steps)
        idx = min(steps - 1, max(0, idx))
        return float(int(spec.lo) + idx)
    raise ValueError(f"unknown point kind {spec.kind!r}")


def build_frame_values(points, elapsed_s: float):
    """Return `[(point_id, value), ...]` for one tick — pure and testable."""
    return [(p.point_id, demo_value(p, elapsed_s)) for p in points]


# ── Plan / scenario (pure) ─────────────────────────────────────────────────────
def legacy_feed_points(gateway_id: str, interval_s: float) -> list[FeedPoint]:
    """The e2e control-demo points, always healthy."""
    return [FeedPoint(spec=p, gateway_id=gateway_id, interval_s=interval_s) for p in DEMO_POINTS]


def load_plan(path: str, scenario: str):
    """Read `feeder-plan.json` → (points, all gateway ids, disconnected gateway ids).

    `healthy` drops every scenario role and connects every gateway.
    """
    if scenario not in SCENARIOS:
        raise ValueError(f"DEMO_SCENARIO must be one of {SCENARIOS}, got {scenario!r}")
    with open(path, encoding="utf-8") as f:
        plan = json.load(f)
    degraded = scenario == "degraded"
    points = []
    for row in plan["points"]:
        period = float(row["period"])
        spec = PointSpec(
            row["pointId"], row["kind"], float(row["base"]), float(row["amplitude"]), period,
            lo=float(row["lo"]), hi=float(row["hi"]),
            # crc32, not hash(): str hashes are salted per process, and the readings must not
            # reshuffle on every feeder restart.
            phase_s=float(zlib.crc32(row["pointId"].encode()) % int(period)) if period >= 1 else 0.0,
        )
        role = row["role"] if degraded else "normal"
        points.append(FeedPoint(
            spec=spec, gateway_id=row["gatewayId"], interval_s=float(row["interval"]), role=role,
            alarm_value=row.get("alarmValue") if role == "alarm" else None,
        ))
    down = set(plan["disconnectedGateways"]) if degraded else set()
    return points, set(plan["gateways"]), down


def connected_gateways(gateways, down) -> list[str]:
    return sorted(set(gateways) - set(down))


def _value(p: FeedPoint, elapsed_s: float) -> float:
    if p.role == "alarm" and p.alarm_value is not None:
        return float(p.alarm_value)
    return demo_value(p.spec, elapsed_s)


def initial_frames(points, down, now_epoch: float):
    """First burst: `[(gateway, point_id, value, timestamp_epoch), ...]`.

    Every streamed point gets a reading now (so /home is populated immediately, even for the 30-min
    energy meters); stale points get their single back-dated reading; missing points and points
    behind a down gateway get nothing.
    """
    frames = []
    for p in points:
        if p.role == "missing" or p.gateway_id in down:
            continue
        ts = now_epoch - p.interval_s * STALE_BACKDATE_INTERVALS if p.role == "stale" else now_epoch
        frames.append((p.gateway_id, p.spec.point_id, _value(p, now_epoch), ts))
    return frames


def due_frames(points, down, last_sent: dict, now_epoch: float):
    """Readings due at `now_epoch`: normal/alarm points whose interval has elapsed since `last_sent`.

    Stale points are never re-sent (their one back-dated reading must stay the latest), missing and
    down-gateway points never at all.
    """
    frames = []
    for p in points:
        if p.role in ("stale", "missing") or p.gateway_id in down:
            continue
        last = last_sent.get(p.spec.point_id)
        if last is None or now_epoch - last >= p.interval_s:
            frames.append((p.gateway_id, p.spec.point_id, _value(p, now_epoch), now_epoch))
    return frames


def group_by_gateway(frames) -> dict[str, list]:
    grouped: dict[str, list] = {}
    for frame in frames:
        grouped.setdefault(frame[0], []).append(frame)
    return grouped


# ── gRPC plumbing (lazy-imported; not exercised by the unit tests) ──────────────
def _load_stubs(proto_path: str):
    """Compile a proto at runtime (no checked-in generated python)."""
    import importlib.util
    import sys
    import tempfile

    from grpc_tools import protoc  # type: ignore

    module = os.path.splitext(os.path.basename(proto_path))[0]
    out = tempfile.mkdtemp(prefix="demo-feeder-proto-")
    proto_dir = os.path.dirname(proto_path)
    rc = protoc.main([
        "protoc", f"-I{proto_dir}",
        f"--python_out={out}", f"--grpc_python_out={out}", proto_path,
    ])
    if rc != 0:
        raise RuntimeError(f"protoc failed (rc={rc}) for {proto_path}")

    def _imp(name, path):
        spec = importlib.util.spec_from_file_location(name, path)
        mod = importlib.util.module_from_spec(spec)
        sys.modules[name] = mod
        spec.loader.exec_module(mod)  # type: ignore
        return mod

    pb2 = _imp(f"{module}_pb2", os.path.join(out, f"{module}_pb2.py"))
    pb2_grpc = _imp(f"{module}_pb2_grpc", os.path.join(out, f"{module}_pb2_grpc.py"))
    return pb2, pb2_grpc


def _iso(epoch: float) -> str:
    from datetime import datetime, timezone

    return datetime.fromtimestamp(epoch, tz=timezone.utc).isoformat()


def _send(stub, pb2, frames) -> int:
    """One client stream carrying one gateway's frames. Returns the server's accepted count."""
    def _gen():
        for gateway_id, point_id, value, ts in frames:
            yield pb2.TelemetryFrame(
                gateway_id=gateway_id, point_id=point_id, value_num=float(value), timestamp=_iso(ts),
            )

    return int(stub.StreamTelemetry(_gen(), timeout=60).accepted)


def run_egress(target: str, gateway_id: str, stubs, stop: threading.Event):
    """Hold one GatewayEgress stream for `gateway_id` (heartbeat → connected) and ack any control."""
    import queue

    import grpc  # type: ignore

    pb2, pb2_grpc = stubs
    backoff = 1.0
    while not stop.is_set():
        outbox: queue.Queue = queue.Queue()
        outbox.put(pb2.EgressUp(hello=pb2.Hello(gateway_id=gateway_id)))

        def _up(outbox=outbox):
            while not stop.is_set():
                try:
                    yield outbox.get(timeout=1.0)
                except queue.Empty:
                    continue

        try:
            with grpc.insecure_channel(target) as channel:
                stub = pb2_grpc.GatewayEgressStub(channel)
                print(f"[demo-feeder] egress open gateway={gateway_id}", flush=True)
                for down in stub.Connect(_up()):
                    backoff = 1.0
                    if down.WhichOneof("m") == "command":
                        cmd = down.command
                        print(f"[demo-feeder] control gateway={gateway_id} point={cmd.point_id} "
                              f"value={cmd.present_value}", flush=True)
                        outbox.put(pb2.EgressUp(result=pb2.ControlResult(
                            control_id=cmd.control_id, success=True,
                            response="demo-feeder: simulated write")))
        except Exception as exc:  # noqa: BLE001 — demo feeder: log and reconnect on any error
            print(f"[demo-feeder] egress gateway={gateway_id} dropped (will reconnect): {exc}", flush=True)
        stop.wait(backoff)
        backoff = min(backoff * 2, 30.0)


def run(target, points, down, tick_s, proto_path, iterations=None):
    """Stream `points` forever: an initial burst (retried until fully accepted), then due readings.

    Right after `up` the point-metadata cache may not have warmed yet (unknown point_id is skipped),
    so a gateway's initial burst is re-sent until the server accepts every frame — otherwise a stale
    point's single back-dated reading could be dropped and the point would show as missing instead.
    """
    import grpc  # type: ignore

    pb2, pb2_grpc = _load_stubs(proto_path)
    roles: dict[str, int] = {}
    for p in points:
        roles[p.role] = roles.get(p.role, 0) + 1
    print(f"[demo-feeder] target={target} points={len(points)} roles={roles} "
          f"down={sorted(down)} tick={tick_s}s", flush=True)

    last_sent: dict[str, float] = {}
    built_at = time.time()
    pending_initial = group_by_gateway(initial_frames(points, down, built_at))
    channel = None
    tick = 0
    while iterations is None or tick < iterations:
        now = time.time()
        try:
            if channel is None:
                channel = grpc.insecure_channel(target)
            stub = pb2_grpc.GatewayIngressStub(channel)

            for gateway_id in sorted(pending_initial):
                # Re-stamp on every retry, keeping each frame's offset from "now" (0, or
                # interval × 4 for a stale point), so a late-accepted burst is still stale on time.
                frames = [(g, pid, v, now - (built_at - ts)) for g, pid, v, ts in pending_initial[gateway_id]]
                accepted = _send(stub, pb2, frames)
                print(f"[demo-feeder] initial gateway={gateway_id} accepted={accepted}/{len(frames)}",
                      flush=True)
                if accepted >= len(frames):
                    for _, pid, _, _ in frames:
                        last_sent[pid] = now
                    del pending_initial[gateway_id]
                elif tick == UNKNOWN_POINTS_HINT_AFTER_TICKS:
                    # Still rejected a minute in: the cache has long warmed, so the twin most likely
                    # does not hold these points — the seed only imports into an EMPTY store (#484).
                    print(f"[demo-feeder] HINT gateway={gateway_id}: points still unknown to the twin. "
                          "If OxiGraph kept an older twin, upload fixtures/demo/twin.ttl via /admin/twin "
                          "(replace) — see docs/guides/demo-walkthrough.md", flush=True)

            ready = [p for p in points if p.gateway_id not in pending_initial]
            sent = 0
            for frames in group_by_gateway(due_frames(ready, down, last_sent, now)).values():
                _send(stub, pb2, frames)
                for _, pid, _, _ in frames:
                    last_sent[pid] = now
                sent += len(frames)
            if tick % 12 == 0:
                print(f"[demo-feeder] tick={tick} sent={sent}", flush=True)
        except Exception as exc:  # noqa: BLE001 — demo feeder: log and retry on any error
            print(f"[demo-feeder] tick={tick} send failed (will retry): {exc}", flush=True)
            if channel is not None:
                channel.close()
            channel = None

        tick += 1
        time.sleep(tick_s)


def main():
    target = os.environ.get("INGRESS_TARGET", DEFAULT_INGRESS_TARGET)
    gateway_id = os.environ.get("GATEWAY_ID", DEFAULT_GATEWAY_ID)
    interval_s = float(os.environ.get("INTERVAL_SECONDS", "5"))
    tick_s = float(os.environ.get("TICK_SECONDS", "5"))
    proto_path = os.environ.get("PROTO_PATH", DEFAULT_PROTO_PATH)
    plan_path = os.environ.get("DEMO_PLAN_PATH", "")
    scenario = os.environ.get("DEMO_SCENARIO", "").strip().lower() or "degraded"
    egress_target = os.environ.get("EGRESS_TARGET", "")

    points = legacy_feed_points(gateway_id, interval_s)
    gateways, down = {gateway_id}, set()
    if plan_path:
        plan_points, plan_gateways, down = load_plan(plan_path, scenario)
        points += plan_points
        gateways |= plan_gateways
        print(f"[demo-feeder] plan={plan_path} scenario={scenario}", flush=True)

    if egress_target:
        stubs = _load_stubs(os.path.join(os.path.dirname(proto_path), "gateway_egress.proto"))
        stop = threading.Event()
        for gw in connected_gateways(gateways, down):
            threading.Thread(target=run_egress, args=(egress_target, gw, stubs, stop),
                             daemon=True, name=f"egress-{gw}").start()

    run(target, points, down, tick_s, proto_path)


if __name__ == "__main__":
    main()
