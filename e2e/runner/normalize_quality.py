#!/usr/bin/env python3
"""Normalize the S2 baseline quality-check result into the KPI gate's {axis, metrics} shape (E1).

Reads `quality-check-result.json` (written by quality_checker.py via s2_baseline.sh) and maps it to the
E1 ingest-throughput KPIs:

  sustained_throughput_ratio = 1 − loss_rate   (delivered / expected; loss_rate = (expected−db)/expected)
  loss_rate                  = loss_rate
  duplicate_rate             = duplicate_rate
  validation_error_rate      = schema_invalid_count / max(db_row_count, 1)

Usage: python normalize_quality.py --result <quality-check-result.json> --out <run-dir>
"""

from __future__ import annotations

import argparse
import json
import os
import sys


def main() -> int:
    ap = argparse.ArgumentParser(description="Normalize S2 quality check → gate {axis, metrics} (E1)")
    ap.add_argument("--result", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    if not os.path.isfile(args.result):
        print(f"[normalize-quality] no result file: {args.result}", file=sys.stderr)
        return 0  # axis not run / no data → gate SKIP, not an error

    with open(args.result) as f:
        d = json.load(f)

    metrics: dict[str, float] = {}
    if "loss_rate" in d:
        loss = float(d["loss_rate"])
        metrics["loss_rate"] = round(loss, 6)
        metrics["sustained_throughput_ratio"] = round(max(0.0, 1.0 - loss), 6)
    if "duplicate_rate" in d:
        metrics["duplicate_rate"] = round(float(d["duplicate_rate"]), 6)
    if "schema_invalid_count" in d and "db_row_count" in d:
        rows = max(int(d["db_row_count"]), 1)
        metrics["validation_error_rate"] = round(int(d["schema_invalid_count"]) / rows, 6)

    os.makedirs(args.out, exist_ok=True)
    out_path = os.path.join(args.out, "E1-normalized.json")
    with open(out_path, "w") as f:
        json.dump({"axis": "E1_ingest_throughput", "metrics": metrics, "source": "quality-checker"}, f, indent=2)
    print(f"[normalize-quality] E1: wrote {len(metrics)} metrics → {out_path}: {metrics}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
