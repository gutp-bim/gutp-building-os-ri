"""E8 — 障害復旧・可用性 + RTO ライブ計測 (#246).

Stops the connector-worker mid-test (outage), restarts it, measures Recovery Time (gRPC ingress accepts
again), then verifies post-recovery ingest integrity (data sent AFTER recovery is not lost). Uses the
TDD-tested pure math in resilience_metrics.

Flow: seed points → `docker stop` connector (t_down) → `docker start` → poll gRPC until a frame is
accepted (healthy_at; RTO) → phase2: stream N frames → flush → quality_checker(parquet, phase2 building)
→ data_loss_under_outage. Emits {axis:E8_resilience, metrics} (data_loss_under_outage gated; rto report).

Usage:
  python s16_resilience_rto.py --out results/E8 [--phase2 2000] [--container building-os.connector-worker]
      [--compose-file docker-compose.oss.yaml] [--ingress localhost:5051] [--oxigraph ...] [--flush-wait 80]
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import s10_pointlist_integrity as s10  # noqa: E402
import resilience_metrics as rm  # noqa: E402
from s15_ingest_throughput import run_quality_checker  # noqa: E402


def _docker(*args, timeout=120) -> None:
    subprocess.run(["docker", *args], check=False, capture_output=True, timeout=timeout)


def main() -> int:
    import grpc  # type: ignore

    ap = argparse.ArgumentParser(description="E8 resilience + RTO live harness (#246)")
    ap.add_argument("--out", default="results/E8")
    ap.add_argument("--phase2", type=int, default=2000, help="post-recovery frame count")
    ap.add_argument("--container", default="building-os.connector-worker")
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--minio-endpoint", default=os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000"))
    ap.add_argument("--flush-wait", type=int, default=80)
    ap.add_argument("--rto-timeout", type=int, default=120)
    args = ap.parse_args()

    pb2, pb2g = s10.load_ingress_stubs()
    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    gw = f"GW-E8-{tag}"
    probe_pid, probe_bldg = f"e8probe-{tag}", f"e8probe-{tag}"
    run_id, bldg = f"e8-{tag}", f"e8-{tag}"
    points = [f"e8pt-{tag}-{i:03d}" for i in range(10)]
    seeded: list[str] = []
    try:
        s10.insert_point(args.oxigraph, probe_pid, gw, probe_bldg); seeded.append(probe_pid)
        for p in points:
            s10.insert_point(args.oxigraph, p, gw, bldg); seeded.append(p)
        if not s10.wait_visible(pb2, pb2g, args.ingress, gw, probe_pid):
            print("probe point not visible — aborting", file=sys.stderr); return 2

        # ── Outage: stop the connector, then restart and time the recovery. ──
        print(f"stopping {args.container} ...")
        _docker("stop", args.container)
        t_down = time.time()
        time.sleep(3)  # observe downtime
        print(f"starting {args.container} ...")
        _docker("start", args.container)

        # Recovery = the gRPC ingress accepts a probe frame again (listener + metadata cache ready).
        healthy_at = None
        deadline = time.time() + args.rto_timeout
        while time.time() < deadline:
            try:
                if s10.stream_frames(pb2, pb2g, args.ingress, [(gw, probe_pid)]) == 1:
                    healthy_at = time.time()
                    break
            except Exception:  # noqa: BLE001 (ingress not up yet → connection refused)
                pass
            time.sleep(1)
        if healthy_at is None:
            print("connector did not recover within RTO timeout — aborting", file=sys.stderr)
            return 2
        print(f"recovered: RTO={healthy_at - t_down:.1f}s")

        # ── Phase 2: post-recovery ingest must not be lost. ──
        now = datetime.now(timezone.utc)

        def gen():
            for i in range(args.phase2):
                yield pb2.TelemetryFrame(gateway_id=gw, point_id=points[i % len(points)],
                                         value=20.0 + (i % 100) / 10.0, timestamp=now.isoformat())
        with grpc.insecure_channel(args.ingress) as ch:
            ack = pb2g.GatewayIngressStub(ch).StreamTelemetry(gen(), timeout=120)
        accepted = int(ack.accepted)
        print(f"phase2 accepted={accepted}; waiting {args.flush_wait}s for flush...")
        time.sleep(args.flush_wait)

        qc = run_quality_checker(run_id, bldg, accepted, args.minio_endpoint)
        persisted = int(qc.get("db_row_count", 0)) if qc else 0

        metrics = rm.build_e8_metrics(down_at=t_down, healthy_at=healthy_at,
                                      phase2_sent=accepted, phase2_persisted=persisted)
        result = {
            "axis": "E8_resilience",
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "config": {"container": args.container, "phase2_sent": accepted, "phase2_persisted": persisted},
            "metrics": metrics,
            "note": "connector-worker を停止→再起動。RTO=gRPC ingress 再受理まで。data_loss_under_outage="
                    "復旧後 phase2 投入分の損失（store-and-forward / 再接続後 publish の非損失）。",
        }
        os.makedirs(args.out, exist_ok=True)
        out_path = os.path.join(args.out, "E8-resilience.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)

        print("E8 resilience results:")
        print(f"  rto_seconds            {metrics['rto_seconds']} (report)")
        print(f"  data_loss_under_outage {metrics['data_loss_under_outage']} -> "
              f"{'PASS' if metrics['data_loss_under_outage'] <= 0.01 else 'FAIL'} (<= 0.01)")
        print(f"Wrote {out_path}")
        return 0 if metrics["data_loss_under_outage"] <= 0.01 else 1
    finally:
        for p in seeded:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        print(f"cleaned up {len(seeded)} seeded points")


if __name__ == "__main__":
    sys.exit(main())
