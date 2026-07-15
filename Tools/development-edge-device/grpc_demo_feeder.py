"""Demo telemetry feeder (#155).

Streams realistic periodic telemetry over the gRPC GatewayIngress for the seeded
demo twin (`GW-SOS-001` / `SOS-PT-001..008`, from `fixtures/e2e/twin.ttl`) so that
`make demo` shows live data on `/resources` → point detail immediately, with no
external gateway.

Design: the value/frame helpers are PURE and import-safe without grpcio (they are
unit-tested in `test_grpc_demo_feeder.py`). `grpc` / `grpc_tools` are imported
lazily inside the send path so the module imports cleanly in a test environment
that only has the standard library. The frame contract mirrors the E5 harness
(`Tools/e2e-performance/s10_pointlist_integrity.py`): a `TelemetryFrame` carries
`gateway_id` + `point_id` + `value` + `timestamp` only; BuildingOS enriches the
static metadata from the twin by `point_id` (#181).
"""

from __future__ import annotations

import math
import os
import time
from dataclasses import dataclass

DEFAULT_GATEWAY_ID = "GW-SOS-001"
DEFAULT_INGRESS_TARGET = "building-os.connector-worker:5051"
DEFAULT_PROTO_PATH = "/proto/gateway_ingress.proto"


@dataclass(frozen=True)
class PointSpec:
    """A demo point and how to synthesize a plausible reading for it.

    `kind` is one of "number" / "boolean" / "enum". Booleans and enums are emitted
    as numeric codes because the ingress contract is numeric (#189). `lo`/`hi`
    bound numbers (clamp) and define the enum code range; 0/0 means unbounded.
    """

    point_id: str
    kind: str
    base: float
    amplitude: float
    period_s: float
    lo: float = 0.0
    hi: float = 0.0


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
    phase = math.sin(2.0 * math.pi * (elapsed_s % spec.period_s) / spec.period_s)
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


# ── gRPC plumbing (lazy-imported; not exercised by the unit tests) ──────────────
def _load_ingress_stubs(proto_path: str):
    """Compile the ingress proto at runtime (no checked-in generated python)."""
    import importlib.util
    import sys
    import tempfile

    from grpc_tools import protoc  # type: ignore

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

    pb2 = _imp("gateway_ingress_pb2", os.path.join(out, "gateway_ingress_pb2.py"))
    pb2_grpc = _imp("gateway_ingress_pb2_grpc", os.path.join(out, "gateway_ingress_pb2_grpc.py"))
    return pb2, pb2_grpc


def run(target, gateway_id, points, interval_s, proto_path, iterations=None):
    """Send one client-stream of all points every `interval_s` seconds, forever.

    Reconnects on failure and keeps going: right after `up` the point-metadata
    cache may not have warmed yet (unknown point_id is skipped), so early ticks can
    report `accepted=0` until a cache miss-refresh picks the seeded points up.
    """
    from datetime import datetime, timezone

    import grpc  # type: ignore

    pb2, pb2_grpc = _load_ingress_stubs(proto_path)
    print(
        f"[demo-feeder] target={target} gateway={gateway_id} "
        f"points={len(points)} interval={interval_s}s",
        flush=True,
    )

    start = time.monotonic()
    tick = 0
    while iterations is None or tick < iterations:
        elapsed = time.monotonic() - start
        timestamp = datetime.now(timezone.utc).isoformat()
        values = build_frame_values(points, elapsed)

        def _gen(values=values, timestamp=timestamp):
            for point_id, value in values:
                yield pb2.TelemetryFrame(
                    gateway_id=gateway_id, point_id=point_id,
                    value=float(value), timestamp=timestamp,
                )

        try:
            with grpc.insecure_channel(target) as channel:
                stub = pb2_grpc.GatewayIngressStub(channel)
                ack = stub.StreamTelemetry(_gen(), timeout=30)
                print(f"[demo-feeder] tick={tick} accepted={ack.accepted}", flush=True)
        except Exception as exc:  # noqa: BLE001 — demo feeder: log and retry on any error
            print(f"[demo-feeder] tick={tick} send failed (will retry): {exc}", flush=True)

        tick += 1
        time.sleep(interval_s)


def main():
    target = os.environ.get("INGRESS_TARGET", DEFAULT_INGRESS_TARGET)
    gateway_id = os.environ.get("GATEWAY_ID", DEFAULT_GATEWAY_ID)
    interval_s = float(os.environ.get("INTERVAL_SECONDS", "5"))
    proto_path = os.environ.get("PROTO_PATH", DEFAULT_PROTO_PATH)
    run(target, gateway_id, DEMO_POINTS, interval_s, proto_path)


if __name__ == "__main__":
    main()
