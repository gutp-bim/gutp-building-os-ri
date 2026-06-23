#!/usr/bin/env python3
"""Normalize a k6 --summary-export JSON into the canonical {axis, metrics} shape the KPI gate
(gate.py) consumes. Maps k6 trend p(95) / rate values to the kpi-thresholds.yaml metric keys for the
k6-driven axes (E3 latest, E4 historical, E6 control). Axes whose harness already emits canonical JSON
(E2, E5) do not use this.

Only metrics the harness actually measures are emitted; KPIs with no source (e.g. latest freshness,
stale replay) are omitted so the gate marks them SKIP rather than FAIL.

Usage: python normalize_k6.py --axis E4 --summary <k6-summary.json> --out <run-dir>
"""

from __future__ import annotations

import argparse
import json
import os
import sys

# axis flag → (kpi-thresholds.yaml axis key, {canonical_metric: (k6_metric, stat, transform)})
# stat: "p95" → p(95); "rate" → rate. transform: None or a callable applied to the raw value.
_AXES = {
    "E3": ("E3_latest_value", {
        "latest_api_p95_ms": ("kpi_latest_duration", "p95", None),
    }),
    "E4": ("E4_historical_query", {
        "warm_24h_1pt_p95_ms": ("kpi_warm_24h_duration", "p95", None),
        "cold_7d_1pt_p95_ms": ("kpi_cold_7d_duration", "p95", None),
        # NOTE: agg_hour_cold_p95_ms is NOT taken from s9. s9's kpi_agg_hour_duration is aggregate-on-read
        # over (often un-compacted) recent data → bimodal p95. The authoritative agg_hour is measured by
        # s14_agg_cache_multipoint against rollup-backed settled hours (production path). Likewise
        # agg_day_cache_hit_p95_ms / multipoint_scaling come from s14. s9's 30d daily is a参考値 only.
    }),
    "E6": ("E6_control_safety", {
        "command_rtt_p95_ms": ("control_submission_duration", "p95", None),
        # success rate = 1 − error rate
        "command_success_rate": ("s6_error_rate", "rate", lambda r: 1.0 - r),
    }),
}

_STAT_KEYS = {"p95": ("p(95)", "p95"), "rate": ("rate",), "count": ("count",), "avg": ("avg",)}


def _metric_value(metrics: dict, name: str, stat: str):
    """k6 --summary-export puts stats either flat under the metric or nested under 'values'."""
    m = metrics.get(name)
    if not isinstance(m, dict):
        return None
    src = m.get("values") if isinstance(m.get("values"), dict) else m
    for key in _STAT_KEYS.get(stat, (stat,)):
        if key in src:
            try:
                return float(src[key])
            except (TypeError, ValueError):
                return None
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description="Normalize k6 summary-export → gate {axis, metrics}")
    ap.add_argument("--axis", required=True, choices=sorted(_AXES))
    ap.add_argument("--summary", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    if not os.path.isfile(args.summary):
        print(f"[normalize] no summary file: {args.summary}", file=sys.stderr)
        return 0  # nothing to normalize (axis may have been skipped) — not an error

    with open(args.summary) as f:
        doc = json.load(f)
    metrics_src = doc.get("metrics", {})
    axis_key, mapping = _AXES[args.axis]

    out_metrics: dict[str, float] = {}
    for canonical, (k6_name, stat, transform) in mapping.items():
        val = _metric_value(metrics_src, k6_name, stat)
        if val is None:
            continue
        out_metrics[canonical] = round(transform(val) if transform else val, 5)

    os.makedirs(args.out, exist_ok=True)
    out_path = os.path.join(args.out, f"{args.axis}-normalized.json")
    with open(out_path, "w") as f:
        json.dump({"axis": axis_key, "metrics": out_metrics, "source": "k6-summary-export"}, f, indent=2)
    print(f"[normalize] {args.axis}: wrote {len(out_metrics)} metrics → {out_path}: {out_metrics}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
