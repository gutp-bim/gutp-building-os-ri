#!/usr/bin/env python3
"""Normalize the S7 resilience result into the KPI gate's {axis, metrics} shape (E8).

Reads `s7-resilience-result.json` (written by s7_resilience_test.py via s7_resilience.sh) and maps the
post-recovery loss to the one gateable E8 KPI:

  data_loss_under_outage = tests.C.phase2.loss_rate   (new publishes after a mid-load restart)

rto_seconds / backlog_drain_seconds / graceful_degradation are `report`-type KPIs (informational), so
they are left for the report, not gated here.

Usage: python normalize_resilience.py --result <s7-resilience-result.json> --out <run-dir>
"""

from __future__ import annotations

import argparse
import json
import os
import sys


def main() -> int:
    ap = argparse.ArgumentParser(description="Normalize S7 resilience → gate {axis, metrics} (E8)")
    ap.add_argument("--result", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    if not os.path.isfile(args.result):
        print(f"[normalize-resilience] no result file: {args.result}", file=sys.stderr)
        return 0  # axis not run → gate SKIP

    with open(args.result) as f:
        d = json.load(f)

    metrics: dict[str, float] = {}
    try:
        phase2_loss = d["tests"]["C"]["phase2"]["loss_rate"]
        metrics["data_loss_under_outage"] = round(float(phase2_loss), 6)
    except (KeyError, TypeError, ValueError):
        pass  # test C not present → leave SKIP

    os.makedirs(args.out, exist_ok=True)
    out_path = os.path.join(args.out, "E8-normalized.json")
    with open(out_path, "w") as f:
        json.dump({"axis": "E8_resilience", "metrics": metrics, "source": "s7-resilience"}, f, indent=2)
    print(f"[normalize-resilience] E8: wrote {len(metrics)} metrics → {out_path}: {metrics}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
