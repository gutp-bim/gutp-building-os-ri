#!/usr/bin/env python3
"""Execute one real multi-building scale stage for s17_multibuilding_scale_sweep.py."""

from __future__ import annotations

import argparse
import json
import math
import os
import socket
import subprocess
import sys
import time
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from pathlib import Path

import requests

sys.path.insert(0, str(Path(__file__).parent))
import quality_checker  # noqa: E402
import s10_pointlist_integrity as ingress_support  # noqa: E402
from twin_hierarchy import TwinHierarchy  # noqa: E402

SBCO = "https://www.sbco.or.jp/ont/"


def _percentile(sorted_values: list[float], p: float) -> float:
    """Nearest-rank percentile; diagnostic reporting, not a statistical guarantee."""
    if not sorted_values:
        return 0.0
    rank = max(0, min(len(sorted_values) - 1, math.ceil(p * len(sorted_values)) - 1))
    return sorted_values[rank]


def _percentile_summary(prefix: str, values: list[float]) -> dict:
    sorted_values = sorted(values)
    return {
        f"{prefix}_p50_ms": round(_percentile(sorted_values, 0.50), 3),
        f"{prefix}_p95_ms": round(_percentile(sorted_values, 0.95), 3),
        f"{prefix}_max_ms": round(sorted_values[-1], 3) if sorted_values else 0.0,
        f"{prefix}_min_ms": round(sorted_values[0], 3) if sorted_values else 0.0,
    }


def measure(topology: list[dict], boundary, *, invalid_per_gateway: int,
            flush_timeout_s: float, poll_interval_s: float,
            include_diagnostics: bool = False) -> dict:
    """Run one scale stage. ``include_diagnostics`` is opt-in (default off, matching the
    pre-existing single-measurement shape every caller and unit test relies on) — when set, adds a
    warm (immediate repeat) measurement and, if the boundary exposes
    ``point_list_milliseconds_concurrent``, a concurrent-gateway p50/p95/max/min breakdown, so a scale
    tier can report where the documented non-linear Point List cost comes from (Phase A of the
    point-list-projection plan) without changing the shape existing callers depend on.
    """
    gateways = sorted({point["gateway_id"] for point in topology})
    buildings = sorted({point["building_id"] for point in topology})
    try:
        boundary.seed(topology)
        boundary.refresh_services()
        cold_durations = boundary.point_list_milliseconds(gateways)
        point_list_ms = max(cold_durations, default=0.0)
        diagnostics: dict = {}
        if include_diagnostics:
            warm_durations = boundary.point_list_milliseconds(gateways)
            diagnostics["point_list_warm_ms"] = round(max(warm_durations, default=0.0), 3)
            concurrent_fn = getattr(boundary, "point_list_milliseconds_concurrent", None)
            if concurrent_fn is not None:
                diagnostics.update(_percentile_summary(
                    "point_list_concurrent", concurrent_fn(gateways)))
        valid_frames = [(point["gateway_id"], point["point_id"]) for point in topology]
        accepted = boundary.ingest(valid_frames)
        invalid_frames = [
            (gateway, f"unknown-{index:04d}-{gateway}")
            for gateway in gateways for index in range(invalid_per_gateway)
        ]
        invalid_accepted = boundary.ingest(invalid_frames)
        rejected = len(invalid_frames) - invalid_accepted
        waited = 0.0
        lake_rows = boundary.lake_rows(buildings)
        while lake_rows < accepted and waited < flush_timeout_s:
            boundary.wait(poll_interval_s)
            waited += poll_interval_s
            lake_rows = boundary.lake_rows(buildings)
        result = {
            "point_list_ms": round(point_list_ms, 3),
            "accepted": accepted,
            "rejected": rejected,
            "expected_accepted": len(valid_frames),
            "expected_rejected": len(invalid_frames),
            "lake_rows": lake_rows,
            "flush_ms": round(waited * 1_000, 3),
        }
        result.update(diagnostics)
        return result
    finally:
        boundary.cleanup()


class RealBoundary:
    def __init__(self, args: argparse.Namespace, run_id: str):
        self.args = args
        self.run_id = run_id
        self._topology: list[dict] = []
        self._pb2 = self._pb2_grpc = None

    def _sparql(self, update: str, timeout: int = 120) -> None:
        response = requests.post(f"{self.args.oxigraph.rstrip('/')}/update", data=update.encode(),
                                 headers={"Content-Type": "application/sparql-update"}, timeout=timeout)
        response.raise_for_status()

    def seed(self, points: list[dict]) -> None:
        """Seed a per-building spatial hierarchy, then the points hanging off it (#300).

        Previously this emitted `sbco:BuildingExt` — a class that is not in the ontology, so the
        building never appeared in `ListBuildings` and no error was raised — and never linked it to a
        Level, leaving every point an orphan by the product's own reachability rules.
        """
        self._topology = points
        buildings = sorted({point["building_id"] for point in points})
        # building_id per hierarchy → a distinct Level name per building. Sharing one would make
        # the sbco:floor literal join (chain C, and what ListDeviceDetails scopes on) match every
        # building's devices for any one building — an N× fan-out inside the timed query.
        hierarchies = {b: TwinHierarchy("perf:s17", building_id=b) for b in buildings}

        spatial: list[str] = []
        for building in buildings:
            spatial.extend(hierarchies[building].triples())
        self._sparql("INSERT DATA {\n" + "\n".join(spatial) + "\n}")

        # One equipment per building carries the spatial anchor; points attach to it via hasPoint.
        points_by_building: dict[str, list[str]] = {}
        for point in points:
            points_by_building.setdefault(point["building_id"], []).append(point["point_id"])

        for offset in range(0, len(points), self.args.seed_batch):
            triples = []
            for point in points[offset:offset + self.args.seed_batch]:
                pid, building, gateway = point["point_id"], point["building_id"], point["gateway_id"]
                uri = f"urn:perf:s17:point:{pid}"
                triples.append(
                    f'<{uri}> a <{SBCO}PointExt> ; <{SBCO}id> "{pid}" ; '
                    f'<{SBCO}name> "{pid}" ; <{SBCO}building> "{building}" ; '
                    f'<{SBCO}writable> false ; <{SBCO}gatewayId> "{gateway}" .'
                )
            self._sparql("INSERT DATA {\n" + "\n".join(triples) + "\n}")

        # hasPoint links last, batched: a building can hold tens of thousands of points, and one
        # INSERT per building would exceed what the endpoint accepts at the top of the scale sweep.
        for building, pids in points_by_building.items():
            hierarchy = hierarchies[building]
            dev_uri = f"urn:perf:s17:dev:{building}"
            dev_props = " ; ".join(
                [
                    f'<{SBCO}id> "DEV-{building}"',
                    f'<{SBCO}name> "Scale Device {building}"',
                    f'<{SBCO}deviceType> "Sensor"',
                    *hierarchy.equipment_props(),
                ]
            )
            self._sparql(
                f"INSERT DATA {{\n  <{dev_uri}> a <{SBCO}EquipmentExt> ; {dev_props} .\n}}"
            )
            for offset in range(0, len(pids), self.args.seed_batch):
                links = "\n".join(
                    f'  <{dev_uri}> <{SBCO}hasPoint> <urn:perf:s17:point:{pid}> .'
                    for pid in pids[offset:offset + self.args.seed_batch]
                )
                self._sparql("INSERT DATA {\n" + links + "\n}")

    def refresh_services(self) -> None:
        env = os.environ.copy()
        env.setdefault("PARQUET_FLUSH_INTERVAL", "1")
        subprocess.run(["docker", "compose", "-f", self.args.compose_file, "restart",
                        "building-os.connector-worker"],
                       check=True, timeout=180, env=env)
        deadline = time.time() + 120
        ingress_host, ingress_port = self.args.ingress.rsplit(":", 1)
        while time.time() < deadline:
            try:
                api_ok = requests.get(f"{self.args.base_url.rstrip('/')}/health", timeout=3).ok
                connector_ok = requests.get(self.args.connector_health, timeout=3).ok
                with socket.create_connection((ingress_host, int(ingress_port)), timeout=3):
                    ingress_ok = True
                if api_ok and connector_ok and ingress_ok:
                    time.sleep(2)
                    return
            except (requests.RequestException, OSError):
                pass
            time.sleep(2)
        raise TimeoutError("API did not become healthy after topology refresh")

    def point_list_milliseconds(self, gateways: list[str]) -> list[float]:
        return [self._point_list_once(gateway) for gateway in gateways]

    # Caps the thread pool regardless of how many gateways are passed in — this only needs enough
    # concurrency to exercise the intended 20-gateway diagnostic case; an unbounded pool would
    # oversubscribe the host if this helper is ever reused with a much larger gateway list.
    MAX_CONCURRENT_POINT_LIST_WORKERS = 32

    def point_list_milliseconds_concurrent(self, gateways: list[str]) -> list[float]:
        """Fire every gateway's Point List request at once (many gateways polling one API process
        concurrently, matching real replica load) instead of the sequential per-gateway loop above,
        and report the raw per-request durations for percentile summarization.
        """
        workers = max(1, min(len(gateways), self.MAX_CONCURRENT_POINT_LIST_WORKERS))
        with ThreadPoolExecutor(max_workers=workers) as pool:
            return list(pool.map(self._point_list_once, gateways))

    def _point_list_once(self, gateway: str) -> float:
        started = time.perf_counter()
        response = requests.get(
            f"{self.args.base_url.rstrip('/')}/gateways/{gateway}/pointlist",
            headers={"X-Gateway-Id": gateway}, timeout=120)
        elapsed = (time.perf_counter() - started) * 1_000
        response.raise_for_status()
        if len(response.json().get("points", [])) != sum(
                point["gateway_id"] == gateway for point in self._topology):
            raise RuntimeError(f"Point List count mismatch for {gateway}")
        return elapsed

    def ingest(self, frames: list[tuple[str, str]]) -> int:
        import grpc  # type: ignore
        if self._pb2 is None:
            self._pb2, self._pb2_grpc = ingress_support.load_ingress_stubs()
        now = datetime.now(timezone.utc).isoformat()

        def generate():
            for gateway, point in frames:
                yield self._pb2.TelemetryFrame(gateway_id=gateway, point_id=point,
                                               value_num=21.5, timestamp=now)
        with grpc.insecure_channel(self.args.ingress) as channel:
            ack = self._pb2_grpc.GatewayIngressStub(channel).StreamTelemetry(generate(), timeout=600)
        return int(ack.accepted)

    def lake_rows(self, buildings: list[str]) -> int:
        return sum(quality_checker.check_lake_parquet(
            self.run_id, self.args.minio_endpoint, self.args.minio_key,
            self.args.minio_secret, self.args.bucket, building=building)["db_row_count"]
                   for building in buildings)

    @staticmethod
    def wait(seconds: float) -> None:
        time.sleep(seconds)

    def cleanup(self) -> None:
        if not self._topology:
            return
        # Devices and the spatial chain are now seeded too, and their subjects survive a
        # point-only delete: the stale hasPoint edges would otherwise accumulate across the sweep's
        # stages (the building ids repeat) and outlive the run for whatever measures the stack next.
        buildings = sorted({point["building_id"] for point in self._topology})
        subjects = [f"<urn:perf:s17:point:{point['point_id']}>" for point in self._topology]
        subjects += [f"<urn:perf:s17:dev:{building}>" for building in buildings]
        for building in buildings:
            subjects += [
                f"<{uri}>"
                for uri in TwinHierarchy("perf:s17", building_id=building).uris()
            ]
        for offset in range(0, len(subjects), self.args.seed_batch):
            values = " ".join(subjects[offset:offset + self.args.seed_batch])
            try:
                self._sparql(
                    f"DELETE {{ ?s ?p ?o }} WHERE {{ VALUES ?s {{ {values} }} ?s ?p ?o }}")
            except Exception as exc:  # noqa: BLE001
                print(f"cleanup warning: {exc}", file=sys.stderr)


def create_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--topology", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--base-url", default=os.getenv("BASE_URL", "http://localhost:5000"))
    parser.add_argument("--ingress", default=os.getenv("INGRESS_TARGET", "localhost:5051"))
    parser.add_argument("--connector-health", default=os.getenv(
        "CONNECTOR_HEALTH_URL", "http://localhost:8081/health/ready"))
    parser.add_argument("--oxigraph", default=os.getenv("OXIGRAPH_URL", "http://localhost:7878"))
    parser.add_argument("--compose-file", default=os.getenv("COMPOSE_FILE", "docker-compose.oss.yaml"))
    parser.add_argument("--minio-endpoint", default=os.getenv("MINIO_ENDPOINT_HOST", "localhost:9000"))
    parser.add_argument("--minio-key", default=os.getenv("MINIO_ACCESS_KEY", "buildingos"))
    parser.add_argument("--minio-secret", default=os.getenv("MINIO_SECRET_KEY", "buildingos123"))
    parser.add_argument("--bucket", default=os.getenv("MINIO_LAKE_BUCKET", "cold"))
    parser.add_argument("--seed-batch", type=int, default=500)
    parser.add_argument("--invalid-per-gateway", type=int, default=10)
    parser.add_argument("--flush-timeout", type=float, default=120)
    parser.add_argument("--poll-interval", type=float, default=10)
    parser.add_argument(
        "--no-diagnostics", action="store_true",
        help="skip the warm-repeat and concurrent-gateway Point List measurements (Phase A); "
             "reports only the single cold measurement, matching pre-#261-diagnostics behaviour.")
    return parser


def main() -> int:
    args = create_parser().parse_args()
    topology = json.loads(Path(args.topology).read_text())
    result = measure(topology, RealBoundary(args, args.run_id),
                     invalid_per_gateway=args.invalid_per_gateway,
                     flush_timeout_s=args.flush_timeout, poll_interval_s=args.poll_interval,
                     include_diagnostics=not args.no_diagnostics)
    Path(args.output).write_text(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
