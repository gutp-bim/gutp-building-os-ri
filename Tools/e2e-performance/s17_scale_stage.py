#!/usr/bin/env python3
"""Execute one real multi-building scale stage for s17_multibuilding_scale_sweep.py."""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

import requests

sys.path.insert(0, str(Path(__file__).parent))
import quality_checker  # noqa: E402
import s10_pointlist_integrity as ingress_support  # noqa: E402

SBCO = "https://www.sbco.or.jp/ont/"


def measure(topology: list[dict], boundary, *, invalid_per_gateway: int,
            flush_timeout_s: float, poll_interval_s: float) -> dict:
    gateways = sorted({point["gateway_id"] for point in topology})
    buildings = sorted({point["building_id"] for point in topology})
    try:
        boundary.seed(topology)
        boundary.refresh_services()
        point_list_ms = max(boundary.point_list_milliseconds(gateways), default=0.0)
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
        return {
            "point_list_ms": round(point_list_ms, 3),
            "accepted": accepted,
            "rejected": rejected,
            "expected_accepted": len(valid_frames),
            "expected_rejected": len(invalid_frames),
            "lake_rows": lake_rows,
            "flush_ms": round(waited * 1_000, 3),
        }
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
        self._topology = points
        buildings = sorted({point["building_id"] for point in points})
        building_triples = "\n".join(
            f'<urn:perf:s17:building:{building}> a <{SBCO}BuildingExt> ; '
            f'<{SBCO}id> "{building}" ; <{SBCO}name> "{building}" .'
            for building in buildings
        )
        self._sparql(f"INSERT DATA {{\n{building_triples}\n}}")
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
        durations = []
        for gateway in gateways:
            started = time.perf_counter()
            response = requests.get(
                f"{self.args.base_url.rstrip('/')}/gateways/{gateway}/pointlist",
                headers={"X-Gateway-Id": gateway}, timeout=120)
            elapsed = (time.perf_counter() - started) * 1_000
            response.raise_for_status()
            if len(response.json().get("points", [])) != sum(
                    point["gateway_id"] == gateway for point in self._topology):
                raise RuntimeError(f"Point List count mismatch for {gateway}")
            durations.append(elapsed)
        return durations

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
        subjects = [f"<urn:perf:s17:point:{point['point_id']}>" for point in self._topology]
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
    return parser


def main() -> int:
    args = create_parser().parse_args()
    topology = json.loads(Path(args.topology).read_text())
    result = measure(topology, RealBoundary(args, args.run_id),
                     invalid_per_gateway=args.invalid_per_gateway,
                     flush_timeout_s=args.flush_timeout, poll_interval_s=args.poll_interval)
    Path(args.output).write_text(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
