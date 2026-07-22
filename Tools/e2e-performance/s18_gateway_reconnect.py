#!/usr/bin/env python3
"""100-Gateway concentrated reconnect and post-recovery traffic evaluation (#262)."""

from __future__ import annotations

import argparse
import asyncio
import importlib.util
import json
import os
import queue
import random
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path


@dataclass(frozen=True)
class Thresholds:
    convergence_ms: float = 10_000
    loss_rate: float = 0
    duplicates: int = 0
    control_success_rate: float = 1
    service_errors: int = 0


def gateway_ids(count: int, run_id: str) -> list[str]:
    if count < 1:
        raise ValueError("gateway count must be positive")
    tag = "".join(ch for ch in run_id.upper() if ch.isalnum())[-10:] or "RUN"
    return [f"GW-S18-{tag}-{index:03d}" for index in range(count)]


def build_topology(count: int, run_id: str) -> list[dict[str, str]]:
    tag = "".join(ch for ch in run_id.lower() if ch.isalnum())
    ids = gateway_ids(count, run_id)
    return [{"point_id": f"s18-{tag}-p{index:03d}",
             "building_id": f"s18-{tag}-b{index % 4:02d}", "gateway_id": gateway}
            for index, gateway in enumerate(ids)]


def reconnect_offsets_ms(count: int, concentration_ms: int, seed: int) -> list[int]:
    if count < 1 or concentration_ms < 0:
        raise ValueError("count must be positive and concentration_ms non-negative")
    rng = random.Random(seed)
    return [rng.randint(0, concentration_ms) for _ in range(count)]


def evaluate(*, gateway_count: int, reconnected: int, convergence_ms: float,
             ingress_accepted: int, ingress_expected: int, ingress_rejected: int,
             lake_rows: int, duplicate_rows: int, controls_accepted: int,
             controls_succeeded: int, service_errors: dict[str, int],
             thresholds: Thresholds) -> dict:
    loss = max(0, ingress_expected - lake_rows)
    loss_rate = loss / ingress_expected if ingress_expected else 0.0
    control_success_rate = controls_succeeded / gateway_count if gateway_count else 0.0
    failures = []
    if reconnected != gateway_count: failures.append("reconnected")
    if convergence_ms > thresholds.convergence_ms: failures.append("convergence_ms")
    if ingress_accepted != ingress_expected or loss_rate > thresholds.loss_rate: failures.append("loss_rate")
    if duplicate_rows > thresholds.duplicates: failures.append("duplicates")
    if controls_accepted != gateway_count or control_success_rate < thresholds.control_success_rate:
        failures.append("control_success_rate")
    if sum(service_errors.values()) > thresholds.service_errors: failures.append("service_errors")
    metrics = {
        "gateway_count": gateway_count, "reconnected": reconnected,
        "convergence_ms": round(convergence_ms, 3),
        "ingress_accepted": ingress_accepted, "ingress_rejected": ingress_rejected,
        "ingress_expected": ingress_expected, "lake_rows": lake_rows, "loss": loss,
        "loss_rate": round(loss_rate, 6), "duplicate_rows": duplicate_rows,
        "controls_accepted": controls_accepted, "controls_succeeded": controls_succeeded,
        "control_success_rate": round(control_success_rate, 6),
        "service_errors": service_errors,
    }
    return {"metrics": metrics, "passed": not failures, "exceeded_thresholds": failures}


def render_markdown(result: dict, run_id: str, concentration_ms: int) -> str:
    m = result["metrics"]
    services = "\n".join(f"| {name} | {count} |" for name, count in m["service_errors"].items())
    return f"""# 100 Gateway reconnect evaluation (#262)

Run: `{run_id}`<br>
Result: **{'PASS' if result['passed'] else 'FAIL'}**<br>
Load: **{m['gateway_count']} Gateway**, reconnect concentration **{concentration_ms} ms**

| KPI | measured |
|:--|--:|
| reconnected | {m['reconnected']}/{m['gateway_count']} |
| convergence | {m['convergence_ms']} ms |
| ingress accepted/rejected | {m['ingress_accepted']}/{m['ingress_rejected']} |
| lake rows / loss / duplicates | {m['lake_rows']} / {m['loss']} / {m['duplicate_rows']} |
| control accepted/succeeded | {m['controls_accepted']}/{m['controls_succeeded']} |

| service | error log count |
|:--|--:|
{services}

Exceeded: {', '.join(result['exceeded_thresholds']) or 'none'}
"""


def load_egress_stubs():
    from grpc_tools import protoc  # type: ignore
    root = Path(__file__).parents[2]
    proto = root / "proto/gateway_egress.proto"
    out = Path(tempfile.mkdtemp(prefix="s18-proto-"))
    if protoc.main(["protoc", f"-I{proto.parent}", f"--python_out={out}",
                    f"--grpc_python_out={out}", str(proto)]) != 0:
        raise RuntimeError("failed to compile gateway_egress.proto")
    sys.path.insert(0, str(out))
    import gateway_egress_pb2 as pb2  # type: ignore
    import gateway_egress_pb2_grpc as pb2_grpc  # type: ignore
    return pb2, pb2_grpc


class EgressClient:
    def __init__(self, target, gateway_id, pb2, pb2_grpc):
        import grpc  # type: ignore
        self.gateway_id, self.pb2 = gateway_id, pb2
        self.channel = grpc.insecure_channel(target)
        self.stub = pb2_grpc.GatewayEgressStub(self.channel)
        self.up: queue.Queue = queue.Queue()
        self.commands = 0
        self.results = 0
        self.error = None
        self.thread = None

    def connect(self):
        self.up.put(self.pb2.EgressUp(hello=self.pb2.Hello(gateway_id=self.gateway_id)))
        def requests():
            while True:
                item = self.up.get()
                if item is None: return
                yield item
        def receive():
            try:
                for down in self.stub.Connect(requests()):
                    if down.HasField("command"):
                        self.commands += 1
                        self.up.put(self.pb2.EgressUp(result=self.pb2.ControlResult(
                            control_id=down.command.control_id, success=True, response="s18-ack")))
                        self.results += 1
            except Exception as exc:  # stream cancellation is expected on disconnect
                self.error = str(exc)
        self.thread = threading.Thread(target=receive, daemon=True)
        self.thread.start()

    def disconnect(self):
        self.up.put(None)
        self.channel.close()
        if self.thread: self.thread.join(timeout=3)


async def publish_controls(nats_url: str, clients: list[EgressClient]) -> int:
    import nats  # type: ignore
    nc = await nats.connect(nats_url)
    accepted = 0
    try:
        for client in clients:
            control_id = str(uuid.uuid4())
            payload = json.dumps({"id": control_id, "PointId": f"s18-{client.gateway_id}",
                                  "Type": "bacnet-sim", "GatewayId": client.gateway_id,
                                  "Body": json.dumps({"value": 22.5, "priority": 8})}).encode()
            try:
                await nc.request(f"building-os.control.request.gw.{client.gateway_id}", payload,
                                 timeout=3)
                accepted += 1
            except Exception:
                pass
    finally:
        await nc.close()
    return accepted


def count_errors(compose_file: str, since: str, output: Path) -> dict[str, int]:
    names = {"gateway-bridge": "building-os.gateway-bridge",
             "connector-worker": "building-os.connector-worker", "api-server": "building-os.api"}
    counts = {}
    for label, service in names.items():
        proc = subprocess.run(["docker", "logs", "--since", since, service],
                              capture_output=True, text=True, check=False)
        (output / f"{label}.log").write_text(proc.stdout + proc.stderr)
        # .NET gRPC reports a client-initiated stream close as informational text containing
        # "Error reading message". Count only actual error-level records (``fail:``), otherwise every
        # intentional disconnect in this scenario would be misclassified as a service failure.
        counts[label] = sum("fail:" in line.lower()
                            for line in (proc.stdout + proc.stderr).splitlines())
    return counts


def create_parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--output", default="Tools/e2e-performance/results")
    p.add_argument("--run-id")
    p.add_argument("--gateways", type=int, default=100)
    p.add_argument("--concentration-ms", type=int, default=500)
    p.add_argument("--egress", default="localhost:5052")
    p.add_argument("--nats", default="nats://localhost:4222")
    p.add_argument("--compose-file", default="docker-compose.oss.yaml")
    p.add_argument("--base-url", default="http://localhost:5000")
    p.add_argument("--ingress", default="localhost:5051")
    p.add_argument("--connector-health", default="http://localhost:8081/health/ready")
    p.add_argument("--oxigraph", default="http://localhost:7878")
    p.add_argument("--minio-endpoint", default="localhost:9000")
    p.add_argument("--minio-key", default="buildingos")
    p.add_argument("--minio-secret", default="buildingos123")
    p.add_argument("--bucket", default="cold")
    p.add_argument("--flush-timeout", type=float, default=120)
    p.add_argument("--no-refresh", action="store_true",
                   help="do not restart connector-worker after seeding (wait for cache miss refresh)")
    p.add_argument("--skip-service-logs", action="store_true",
                   help="record unavailable log counters when Docker CLI is outside the runner")
    p.add_argument("--max-convergence-ms", type=float, default=10_000)
    return p


def main() -> int:
    args = create_parser().parse_args()
    run_id = args.run_id or datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-s18")
    output = Path(args.output) / run_id
    output.mkdir(parents=True, exist_ok=True)
    started = datetime.now(timezone.utc).isoformat()
    pb2, pb2_grpc = load_egress_stubs()
    ids = gateway_ids(args.gateways, run_id)
    # Reuse #261's real public ingress + Parquet boundary. One deterministic writable-independent
    # telemetry point per gateway is enough to prove post-reconnect data-plane recovery.
    sys.path.insert(0, str(Path(__file__).parent))
    import s17_scale_stage  # type: ignore
    topology = build_topology(args.gateways, run_id)
    boundary_args = argparse.Namespace(
        oxigraph=args.oxigraph, seed_batch=500, compose_file=args.compose_file,
        ingress=args.ingress, base_url=args.base_url, connector_health=args.connector_health,
        minio_endpoint=args.minio_endpoint, minio_key=args.minio_key,
        minio_secret=args.minio_secret, bucket=args.bucket)
    boundary = s17_scale_stage.RealBoundary(boundary_args, run_id)
    boundary.seed(topology)
    if args.no_refresh:
        time.sleep(35)
    else:
        boundary.refresh_services()
    initial = [EgressClient(args.egress, gid, pb2, pb2_grpc) for gid in ids]
    for client in initial: client.connect()
    time.sleep(2)
    for client in initial: client.disconnect()

    clients = [EgressClient(args.egress, gid, pb2, pb2_grpc) for gid in ids]
    offsets = reconnect_offsets_ms(args.gateways, args.concentration_ms, seed=18)
    begin = time.perf_counter()
    threads = []
    for client, offset in zip(clients, offsets):
        thread = threading.Thread(target=lambda c=client, o=offset: (time.sleep(o / 1000), c.connect()))
        thread.start(); threads.append(thread)
    for thread in threads: thread.join()
    time.sleep(2)
    controls_accepted = asyncio.run(publish_controls(args.nats, clients))
    convergence_ms = (time.perf_counter() - begin) * 1_000
    deadline = time.time() + 10
    while sum(c.results for c in clients) < args.gateways and time.time() < deadline: time.sleep(.1)
    controls_succeeded = sum(c.results for c in clients)
    valid = [(point["gateway_id"], point["point_id"]) for point in topology]
    invalid = [(gid, f"unknown-s18-{index:03d}") for index, gid in enumerate(ids)]
    ingress_accepted = boundary.ingest(valid)
    ingress_rejected = len(invalid) - boundary.ingest(invalid)
    buildings = sorted({point["building_id"] for point in topology})
    deadline = time.time() + args.flush_timeout
    lake_rows = boundary.lake_rows(buildings)
    while lake_rows < ingress_accepted and time.time() < deadline:
        time.sleep(2)
        lake_rows = boundary.lake_rows(buildings)
    duplicate_rows = max(0, lake_rows - ingress_accepted)
    service_errors = ({"gateway-bridge": 0, "connector-worker": 0, "api-server": 0}
                      if args.skip_service_logs else count_errors(args.compose_file, started, output))
    result = evaluate(gateway_count=args.gateways, reconnected=controls_accepted,
                      convergence_ms=convergence_ms, ingress_accepted=ingress_accepted,
                      ingress_expected=len(valid), ingress_rejected=ingress_rejected,
                      lake_rows=lake_rows, duplicate_rows=duplicate_rows,
                      controls_accepted=controls_accepted, controls_succeeded=controls_succeeded,
                      service_errors=service_errors,
                      thresholds=Thresholds(convergence_ms=args.max_convergence_ms))
    for client in clients: client.disconnect()
    boundary.cleanup()
    summary = {"run_id": run_id, "conditions": vars(args), "thresholds": asdict(Thresholds(
        convergence_ms=args.max_convergence_ms)), **result}
    (output / "kpi-summary.json").write_text(json.dumps(summary, indent=2))
    (output / "report.md").write_text(render_markdown(result, run_id, args.concentration_ms))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
