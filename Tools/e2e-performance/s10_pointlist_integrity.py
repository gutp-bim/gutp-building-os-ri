#!/usr/bin/env python3
"""E5 — Point List / Digital Twin 整合性 (#243).

Drives the gRPC ``GatewayIngress`` (point-id canonical contract, #181) to measure the contract
guarantees that are unique to this architecture: a frame is accepted ONLY when its ``point_id`` is in
the shared point list AND its ``gateway_id`` owns that point in the digital twin. Everything else is
rejected (skipped + metered) by ``GatewayIngressService``.

Because ``StreamAck.accepted`` is per-stream, each category is sent on its OWN client-stream so the
accept count attributes cleanly to that category — no Prometheus scrape required:

  * valid      : N frames (GW_A, owned points)        → expect accepted == N  (resolution success)
  * unknown    : N frames (GW_A, never-seeded ids)    → expect accepted == 0  (unknown rejection)
  * ownership  : N frames (GW_A, points owned by GW_B)→ expect accepted == 0  (ownership rejection)
  * missing    : N frames (empty gateway/point id)    → expect accepted == 0  (input guard)
  * remap      : move a point GW_A→GW_B, flush cache  → (GW_A,p) rejected & (GW_B,p) accepted

The twin is seeded directly via SPARQL (insert/delete/remap) so the harness owns its fixtures and
cleans them up. ``IPointMetadataCache`` has a 5-min TTL + 30s miss-refresh, so newly-seeded points
become visible within ~30s (verified by polling); a *remap* of an existing id is not a cache miss, so
the connector-worker is restarted to force a fresh load (deterministic, ~15s) unless --no-restart.

Twin-lookup latency is measured as the p95 of ``GET /telemetries/query?...&latest=true`` (resolves the
point through the twin metadata path); it is an upper bound (also touches Hot KV / lake) and reported
as such.

Usage:
  python s10_pointlist_integrity.py --out results/E5 [--frames 200] [--ingress localhost:5051]
                                    [--oxigraph http://localhost:7878] [--base-url http://localhost:5000]
                                    [--no-restart]
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import logging
import os
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request
from datetime import datetime, timezone

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger("e5")

SBCO = "https://www.sbco.or.jp/ont/"
REPO_PROTO = os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "proto",
    "gateway_ingress.proto")


# ── proto compiled at runtime (no checked-in generated python) ────────────────────────────────────
def load_ingress_stubs():
    from grpc_tools import protoc  # type: ignore

    out = tempfile.mkdtemp(prefix="e5-proto-")
    proto_dir = os.path.dirname(REPO_PROTO)
    rc = protoc.main([
        "protoc", f"-I{proto_dir}",
        f"--python_out={out}", f"--grpc_python_out={out}", REPO_PROTO,
    ])
    if rc != 0:
        raise RuntimeError(f"protoc failed (rc={rc}) for {REPO_PROTO}")

    def _imp(name, path):
        spec = importlib.util.spec_from_file_location(name, path)
        mod = importlib.util.module_from_spec(spec)
        sys.modules[name] = mod
        spec.loader.exec_module(mod)  # type: ignore
        return mod

    pb2 = _imp("gateway_ingress_pb2", os.path.join(out, "gateway_ingress_pb2.py"))
    pb2_grpc = _imp("gateway_ingress_pb2_grpc", os.path.join(out, "gateway_ingress_pb2_grpc.py"))
    return pb2, pb2_grpc


# ── SPARQL twin fixtures ──────────────────────────────────────────────────────────────────────────
def sparql_update(oxigraph: str, update: str) -> None:
    req = urllib.request.Request(
        f"{oxigraph.rstrip('/')}/update", data=update.encode("utf-8"),
        headers={"Content-Type": "application/sparql-update"}, method="POST")
    with urllib.request.urlopen(req, timeout=30) as resp:  # noqa: S310 (local dev endpoint)
        if resp.status not in (200, 204):
            raise RuntimeError(f"OxiGraph update returned {resp.status}")


def insert_point(oxigraph: str, pid: str, gateway: str, building: str) -> None:
    pt, dev = f"urn:perf:ingpt:{pid}", f"urn:perf:ingdev:{pid}"
    sparql_update(oxigraph, (
        f"INSERT DATA {{\n"
        f'  <{pt}> a <{SBCO}PointExt> ; <{SBCO}id> "{pid}" ; <{SBCO}name> "{pid}" ; '
        f'<{SBCO}building> "{building}" ; <{SBCO}writable> false ; <{SBCO}gatewayId> "{gateway}" .\n'
        f'  <{dev}> a <{SBCO}EquipmentExt> ; <{SBCO}id> "DEV-{pid}" ; <{SBCO}name> "Dev {pid}" ; '
        f"<{SBCO}hasPoint> <{pt}> .\n}}"))


def delete_point(oxigraph: str, pid: str) -> None:
    pt, dev = f"urn:perf:ingpt:{pid}", f"urn:perf:ingdev:{pid}"
    sparql_update(oxigraph, f"DELETE WHERE {{ <{pt}> ?p ?o }};\nDELETE WHERE {{ <{dev}> ?p ?o }}")


def remap_gateway(oxigraph: str, pid: str, old_gw: str, new_gw: str) -> None:
    pt = f"urn:perf:ingpt:{pid}"
    sparql_update(oxigraph, (
        f'DELETE {{ <{pt}> <{SBCO}gatewayId> "{old_gw}" }} '
        f'INSERT {{ <{pt}> <{SBCO}gatewayId> "{new_gw}" }} WHERE {{ <{pt}> a <{SBCO}PointExt> }}'))


# ── gRPC ingest ────────────────────────────────────────────────────────────────────────────────────
def stream_frames(pb2, pb2_grpc, target: str, frames: list[tuple[str, str]]) -> int:
    """Open ONE client-stream, send (gateway_id, point_id) frames, return StreamAck.accepted."""
    import grpc  # type: ignore

    now = datetime.now(timezone.utc).isoformat()

    def gen():
        for gw, pid in frames:
            yield pb2.TelemetryFrame(gateway_id=gw, point_id=pid, value=21.5, timestamp=now)

    with grpc.insecure_channel(target) as ch:
        stub = pb2_grpc.GatewayIngressStub(ch)
        ack = stub.StreamTelemetry(gen(), timeout=60)
        return int(ack.accepted)


def wait_visible(pb2, pb2_grpc, target: str, gw: str, pid: str, timeout_s: float = 45.0) -> bool:
    """Poll a single valid frame until the metadata cache picks the seeded point up (miss-refresh)."""
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        if stream_frames(pb2, pb2_grpc, target, [(gw, pid)]) == 1:
            return True
        time.sleep(3)
    return False


# ── twin lookup latency (proxy via latest query) ────────────────────────────────────────────────────
def measure_twin_lookup_p95(base_url: str, pids: list[str], samples: int = 60) -> float | None:
    import urllib.error

    durations: list[float] = []
    for i in range(samples):
        pid = pids[i % len(pids)]
        url = f"{base_url.rstrip('/')}/telemetries/query?pointId={urllib.parse.quote(pid)}&latest=true"
        t0 = time.perf_counter()
        try:
            with urllib.request.urlopen(url, timeout=10) as resp:  # noqa: S310
                resp.read()
        except urllib.error.HTTPError:
            pass  # 404 (no data yet) still exercised the twin resolve path
        except Exception:  # noqa: BLE001
            continue
        durations.append((time.perf_counter() - t0) * 1000.0)
    if not durations:
        return None
    durations.sort()
    return durations[min(len(durations) - 1, int(len(durations) * 0.95))]


import urllib.parse  # noqa: E402  (after fn defs, before main use)


def restart_connector(compose_file: str) -> bool:
    try:
        subprocess.run(["docker", "compose", "-f", compose_file, "restart", "building-os.connector-worker"],
                       check=True, capture_output=True, timeout=120)
        time.sleep(12)  # let the worker reconnect NATS + rebuild the metadata cache
        return True
    except Exception as e:  # noqa: BLE001
        logger.warning("connector-worker restart failed: %s", e)
        return False


def main() -> int:
    ap = argparse.ArgumentParser(description="E5 point-list / twin integrity harness")
    ap.add_argument("--out", default="results/E5")
    ap.add_argument("--frames", type=int, default=200)
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--base-url", default=os.environ.get("BASE_URL", "http://localhost:5000"))
    ap.add_argument("--compose-file", default=os.environ.get("COMPOSE_FILE", "docker-compose.oss.yaml"))
    ap.add_argument("--no-restart", action="store_true", help="skip connector-worker restart for remap")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    pb2, pb2_grpc = load_ingress_stubs()

    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    gw_a, gw_b, building = f"GW-E5A-{tag}", f"GW-E5B-{tag}", f"e5-{tag}"
    n = args.frames
    valid_pts = [f"e5pt-A-{tag}-{i:04d}" for i in range(n)]
    owned_by_b = [f"e5pt-B-{tag}-{i:04d}" for i in range(n)]
    unknown_pts = [f"e5pt-UNK-{tag}-{i:04d}" for i in range(n)]  # never seeded

    seeded: list[str] = []
    try:
        logger.info("Seeding %d points owned by %s and %d owned by %s (building=%s)",
                    n, gw_a, n, gw_b, building)
        for p in valid_pts:
            insert_point(args.oxigraph, p, gw_a, building); seeded.append(p)
        for p in owned_by_b:
            insert_point(args.oxigraph, p, gw_b, building); seeded.append(p)

        # Wait for the metadata cache to see the new points (miss-refresh ≤ ~30s).
        if not wait_visible(pb2, pb2_grpc, args.ingress, gw_a, valid_pts[0]):
            logger.error("seeded points never became visible to the ingress cache — aborting")
            return 2

        # 1) valid — owned points accepted.
        acc_valid = stream_frames(pb2, pb2_grpc, args.ingress, [(gw_a, p) for p in valid_pts])
        # 2) unknown — never-seeded ids rejected.
        acc_unknown = stream_frames(pb2, pb2_grpc, args.ingress, [(gw_a, p) for p in unknown_pts])
        # 3) ownership — points owned by GW_B sent from GW_A rejected.
        acc_owner = stream_frames(pb2, pb2_grpc, args.ingress, [(gw_a, p) for p in owned_by_b])
        # 4) missing id — input guard.
        acc_missing = stream_frames(pb2, pb2_grpc, args.ingress, [("", "") for _ in range(n)])

        # 5) remap correctness: move valid_pts[0] from GW_A → GW_B, flush cache, re-probe.
        remap_pt = valid_pts[0]
        remap_supported = True
        remap_old_rejected = remap_new_accepted = None
        remap_gateway(args.oxigraph, remap_pt, gw_a, gw_b)
        flushed = False if args.no_restart else restart_connector(args.compose_file)
        if not flushed:
            logger.warning("cache not flushed (no-restart or restart failed); remap needs ≤5min TTL — "
                           "marking remap as not-evaluated")
            remap_supported = False
        else:
            wait_visible(pb2, pb2_grpc, args.ingress, gw_b, owned_by_b[0])  # warm cache post-restart
            remap_old_rejected = stream_frames(pb2, pb2_grpc, args.ingress, [(gw_a, remap_pt)]) == 0
            remap_new_accepted = stream_frames(pb2, pb2_grpc, args.ingress, [(gw_b, remap_pt)]) == 1

        twin_lookup_p95 = measure_twin_lookup_p95(args.base_url, valid_pts[:20])

        resolution_success = acc_valid / n if n else 0.0
        unknown_rejection = 1.0 - (acc_unknown / n if n else 0.0)
        ownership_rejection = 1.0 - (acc_owner / n if n else 0.0)
        remap_correct = (bool(remap_old_rejected) and bool(remap_new_accepted)) if remap_supported else None

        result = {
            "axis": "E5_pointlist_integrity",
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "frames_per_category": n,
            "accepted": {"valid": acc_valid, "unknown": acc_unknown,
                         "ownership": acc_owner, "missing": acc_missing},
            "metrics": {
                "point_resolution_success": round(resolution_success, 5),
                "unknown_point_rejection": round(unknown_rejection, 5),
                "ownership_rejection": round(ownership_rejection, 5),
                "remapping_correctness": (1.0 if remap_correct else 0.0) if remap_supported else None,
                "twin_lookup_p95_ms": round(twin_lookup_p95, 2) if twin_lookup_p95 is not None else None,
            },
            "thresholds": {
                "point_resolution_success": ">= 0.999",
                "unknown_point_rejection": "== 1.0",
                "ownership_rejection": "== 1.0",
                "remapping_correctness": "== 1.0",
                "twin_lookup_p95_ms": "< 50",
            },
            "remap_evaluated": remap_supported,
            "twin_lookup_note": "proxy: GET /telemetries/query?latest (resolves via twin metadata); "
                                "upper bound (also touches Hot KV / lake)",
        }

        def ok(name, val, pred):
            status = "PASS" if pred else ("SKIP" if val is None else "FAIL")
            logger.info("  %-28s %-10s -> %s", name, val, status)
            return status

        logger.info("E5 results:")
        statuses = {
            "point_resolution_success": ok("point_resolution_success", result["metrics"]["point_resolution_success"],
                                           resolution_success >= 0.999),
            "unknown_point_rejection": ok("unknown_point_rejection", result["metrics"]["unknown_point_rejection"],
                                          unknown_rejection == 1.0),
            "ownership_rejection": ok("ownership_rejection", result["metrics"]["ownership_rejection"],
                                      ownership_rejection == 1.0),
            "remapping_correctness": ok("remapping_correctness", result["metrics"]["remapping_correctness"],
                                        remap_correct is True),
            "twin_lookup_p95_ms": ok("twin_lookup_p95_ms", result["metrics"]["twin_lookup_p95_ms"],
                                     twin_lookup_p95 is not None and twin_lookup_p95 < 50),
        }
        result["status"] = statuses
        out_path = os.path.join(args.out, "E5-pointlist-integrity.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)
        logger.info("Wrote %s", out_path)

        hard_fail = any(s == "FAIL" for k, s in statuses.items()
                        if k in ("point_resolution_success", "unknown_point_rejection", "ownership_rejection"))
        return 1 if hard_fail else 0
    finally:
        for p in seeded:
            try:
                delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        logger.info("cleaned up %d seeded points", len(seeded))


if __name__ == "__main__":
    sys.exit(main())
