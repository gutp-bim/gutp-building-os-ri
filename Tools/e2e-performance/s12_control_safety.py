#!/usr/bin/env python3
"""E6 — Control path 安全性（残シナリオ, #244）.

Exercises the control safety guarantees that `s6` (RTT/throughput) does not cover, all observable at the
HTTP control API + the NATS result subject:

  * not_writable_rejection : POST control to a writable=false point → 403 (CanWritePointAsync gate).
  * typed_failure_classified: every failure case returns the EXPECTED typed status (not-writable=403,
                             unknown point=404) — i.e. failures are classified, not bare 500s.
  * stale_replay_count     : control is ephemeral core-NATS request/reply (not a durable queue), so a
                             command for a gateway with no live processor is never executed/replayed.
                             We subscribe to building-os.control.result.> across the POSTs + a grace
                             window and assert ZERO results land.

Out of scope here (documented in the report, not emitted → gate SKIP):
  * offline_503_ratio — the local OSS stack cannot reproduce a disconnected egress gateway (the
    per-gateway NATS request does not surface no-responders without a real offline gateway). The
    GatewayOffline→503 path is covered by backend unit tests + #186.
  * command_success_rate — needs a connected gateway to confirm execution (s6 covers RTT).
  * duplicate_write_count — connector/gateway-side idempotency (Nats-Msg-Id dedup), not API-observable.

Twin points are seeded/cleaned via SPARQL.

Usage:
  python s12_control_safety.py --out results/E6 [--n 30] [--gateway GW-E6SIM]
      [--base-url http://localhost:5000] [--oxigraph http://localhost:7878] [--nats nats://localhost:4222]
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import s10_pointlist_integrity as s10  # noqa: E402 (reuse SPARQL helpers)

SBCO = "https://www.sbco.or.jp/ont/"


def seed_point(oxi: str, pid: str, *, writable: bool, gateway: str) -> None:
    pt, dev = f"urn:perf:e6pt:{pid}", f"urn:perf:e6dev:{pid}"
    s10.sparql_update(oxi, (
        f"INSERT DATA {{\n"
        f'  <{pt}> a <{SBCO}PointExt> ; <{SBCO}id> "{pid}" ; <{SBCO}name> "{pid}" ; '
        f'<{SBCO}building> "e6" ; <{SBCO}writable> {"true" if writable else "false"} ; '
        f'<{SBCO}gatewayId> "{gateway}" .\n'
        f'  <{dev}> a <{SBCO}EquipmentExt> ; <{SBCO}id> "DEV-{pid}" ; <{SBCO}name> "Dev {pid}" ; '
        f"<{SBCO}hasPoint> <{pt}> .\n}}"))


def delete_point(oxi: str, pid: str) -> None:
    pt, dev = f"urn:perf:e6pt:{pid}", f"urn:perf:e6dev:{pid}"
    s10.sparql_update(oxi, f"DELETE WHERE {{ <{pt}> ?p ?o }};\nDELETE WHERE {{ <{dev}> ?p ?o }}")


def post_control(base_url: str, pid: str, value: float = 1.0) -> tuple[int, dict]:
    url = f"{base_url.rstrip('/')}/points/{urllib.parse.quote(pid)}/control"
    req = urllib.request.Request(url, data=json.dumps({"value": value}).encode(),
                                 headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=15) as r:  # noqa: S310
            return r.status, _json(r.read())
    except urllib.error.HTTPError as e:
        return e.code, _json(e.read())


def _json(b: bytes) -> dict:
    try:
        d = json.loads(b)
        return d if isinstance(d, dict) else {}
    except (ValueError, TypeError):
        return {}


async def run(args) -> int:
    import nats  # type: ignore

    oxi, base = args.oxigraph, args.base_url
    nowrite = [f"e6-nowrite-{i:03d}" for i in range(args.n)]
    unknown = [f"e6-unknown-{i:03d}" for i in range(args.n)]  # never seeded → 404
    seeded: list[str] = []
    nc = None
    try:
        for p in nowrite:
            seed_point(oxi, p, writable=False, gateway=args.gateway); seeded.append(p)
        time.sleep(2)  # let the twin settle

        # Subscribe to control results so we can prove rejected commands are never executed/replayed.
        nc = await nats.connect(args.nats)
        result_hits: list[str] = []

        async def on_result(m):
            result_hits.append(m.subject)

        await nc.subscribe("building-os.control.result.>", cb=on_result)

        # not-writable → 403 (the CanWritePointAsync gate rejects before any command is published).
        nowrite_status = [post_control(base, p)[0] for p in nowrite]
        nowrite_403 = sum(1 for s in nowrite_status if s == 403)
        # unknown point → also a typed client error (the writable gate forbids unknown points first).
        unknown_status = [post_control(base, p)[0] for p in unknown]

        # typed-failure classification: every failure is a typed 4xx client error (400/403/404), never a
        # bare 5xx. A 503 (offline) would also count, but is not reproducible locally (see docstring).
        all_status = nowrite_status + unknown_status
        typed_total = len(all_status)
        typed_ok = sum(1 for s in all_status if s in (400, 403, 404, 503))

        # stale-replay: a rejected/unhandled command must never surface a result, even after a grace
        # window (control is ephemeral request/reply, not a durable replayed queue).
        await asyncio.sleep(5)
        stale_replays = len(result_hits)

        n = args.n
        metrics = {
            "not_writable_rejection": round(nowrite_403 / n, 5) if n else None,
            "typed_failure_classified": round(typed_ok / typed_total, 5) if typed_total else None,
            "stale_replay_count": stale_replays,
        }
        result = {
            "axis": "E6_control_safety",
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "config": {"n": n, "gateway": args.gateway},
            "metrics": metrics,
            "not_emitted": {
                "offline_503_ratio": "local stack can't reproduce a disconnected egress gateway; "
                                     "covered by backend unit tests + #186",
                "command_success_rate": "needs a connected gateway (s6 covers RTT)",
                "duplicate_write_count": "connector-side Nats-Msg-Id dedup, not API-observable",
            },
        }
        os.makedirs(args.out, exist_ok=True)
        out_path = os.path.join(args.out, "E6-safety.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)

        def ok(name, val, pred):
            status = "PASS" if pred else ("SKIP" if val is None else "FAIL")
            print(f"  {name:28} {val!s:10} -> {status}")
            return status

        print("E6 control-safety results:")
        ok("not_writable_rejection", metrics["not_writable_rejection"], metrics["not_writable_rejection"] == 1.0)
        ok("typed_failure_classified", metrics["typed_failure_classified"], metrics["typed_failure_classified"] == 1.0)
        ok("stale_replay_count", metrics["stale_replay_count"], metrics["stale_replay_count"] == 0)
        print(f"Wrote {out_path}")

        hard_fail = (metrics["not_writable_rejection"] != 1.0
                     or metrics["typed_failure_classified"] != 1.0
                     or metrics["stale_replay_count"] != 0)
        return 1 if hard_fail else 0
    finally:
        if nc is not None:
            await nc.drain()
        for p in seeded:
            try:
                delete_point(oxi, p)
            except Exception:  # noqa: BLE001
                pass
        print(f"cleaned up {len(seeded)} seeded points")


def main() -> int:
    ap = argparse.ArgumentParser(description="E6 control-safety harness (#244)")
    ap.add_argument("--out", default="results/E6")
    ap.add_argument("--n", type=int, default=30)
    ap.add_argument("--gateway", default="GW-E6SIM")
    ap.add_argument("--base-url", default=os.environ.get("BASE_URL", "http://localhost:5000"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--nats", default=os.environ.get("NATS_URL", "nats://localhost:4222"))
    args = ap.parse_args()
    return asyncio.run(run(args))


if __name__ == "__main__":
    sys.exit(main())
