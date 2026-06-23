#!/usr/bin/env python3
"""E0 — KPI gate. Compares each axis's result JSON against e2e/kpi-thresholds.yaml and emits a
consolidated pass/fail report (Markdown + JSON), highlighting the paper headline metrics.

Each axis harness writes a normalized result file somewhere under the run dir, shaped:

    { "axis": "E5_pointlist_integrity", "metrics": { "<metric>": <number|null>, ... }, ... }

The gate scans the run dir recursively for *.json carrying an "axis" field, indexes them by that
field, then for every axis/metric in the thresholds yaml applies the comparison operator:

    lt (<)  lte (<=)  gt (>)  gte (>=)  eq (==)        → PASS/FAIL
    report                                             → INFO (value reported, never fails)
    sublinear / unknown op / threshold value == null   → SKIP (cannot gate mechanically)

A metric present in the yaml but absent from the result JSON (axis not run, or harness emitted no
value) is SKIP, not FAIL — so a partial run still gates the axes it produced. Exit code is non-zero
only when at least one comparable metric FAILs.

Usage: python gate.py <run-dir> [--thresholds e2e/kpi-thresholds.yaml]
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys

import yaml

_OPS = {
    "lt": ("<", lambda a, b: a < b),
    "lte": ("<=", lambda a, b: a <= b),
    "gt": (">", lambda a, b: a > b),
    "gte": (">=", lambda a, b: a >= b),
    "eq": ("==", lambda a, b: a == b),
}


def load_results(run_dir: str) -> dict[str, dict]:
    """axis field → flattened metrics dict, for every *.json under run_dir that has an 'axis'."""
    out: dict[str, dict] = {}
    for path in glob.glob(os.path.join(run_dir, "**", "*.json"), recursive=True):
        try:
            with open(path) as f:
                doc = json.load(f)
        except (ValueError, OSError):
            continue
        if not isinstance(doc, dict):
            continue
        axis = doc.get("axis")
        if not axis:
            continue
        metrics = doc.get("metrics", {})
        if isinstance(metrics, dict):
            # last writer wins if an axis produced multiple files (keep the richer one)
            merged = out.get(axis, {})
            merged.update({k: v for k, v in metrics.items() if v is not None or k not in merged})
            out[axis] = merged
    return out


def evaluate(thresholds: dict, results: dict[str, dict]) -> tuple[list[dict], list[str]]:
    headline = set(thresholds.get("headline", []))
    rows: list[dict] = []
    failed: list[str] = []
    for axis, metrics in thresholds.get("axes", {}).items():
        got = results.get(axis, {})
        for metric, spec in metrics.items():
            key = f"{axis}.{metric}"
            op = spec.get("op")
            threshold = spec.get("value")
            actual = got.get(metric)
            star = "⭐" if key in headline else ""

            if op == "report":
                status = "INFO"
            elif op not in _OPS or threshold is None:
                status = "SKIP"  # sublinear / unknown op / dynamic (null) threshold
            elif actual is None:
                status = "SKIP"  # axis not run or no value emitted
            else:
                ok = _OPS[op][1](actual, threshold)
                status = "PASS" if ok else "FAIL"
                if not ok:
                    failed.append(key)

            sym = _OPS[op][0] if op in _OPS else (op or "")
            rows.append({
                "key": key, "axis": axis, "metric": metric, "headline": bool(star),
                "actual": actual, "op": sym, "threshold": threshold,
                "unit": spec.get("unit", ""), "status": status,
            })
    return rows, failed


def write_report(run_dir: str, rows: list[dict], failed: list[str]) -> str:
    counts = {s: sum(1 for r in rows if r["status"] == s) for s in ("PASS", "FAIL", "SKIP", "INFO")}
    lines = [
        "# Building OS E2E — KPI Gate レポート",
        "",
        f"run: `{os.path.basename(run_dir.rstrip('/'))}`  ",
        f"結果: **{'PASS' if not failed else 'FAIL'}**  "
        f"(PASS {counts['PASS']} / FAIL {counts['FAIL']} / SKIP {counts['SKIP']} / INFO {counts['INFO']})",
        "",
        "## ヘッドライン指標（論文強調）",
        "",
        "| 指標 | 実測 | 判定 |",
        "|---|--:|---|",
    ]
    for r in (x for x in rows if x["headline"]):
        a = "—" if r["actual"] is None else f"{r['actual']} {r['unit']}".strip()
        lines.append(f"| ⭐ {r['key']} | {a} | {r['status']} |")
    lines += ["", "## 全 KPI", "", "| 指標 | 実測 | 閾値 | 判定 |", "|---|--:|--:|---|"]
    for r in rows:
        a = "—" if r["actual"] is None else f"{r['actual']} {r['unit']}".strip()
        thr = "—" if r["threshold"] is None else f"{r['op']} {r['threshold']}".strip()
        star = "⭐ " if r["headline"] else ""
        lines.append(f"| {star}{r['key']} | {a} | {thr} | {r['status']} |")
    lines.append("")
    md = "\n".join(lines)
    path = os.path.join(run_dir, "kpi-report.md")
    with open(path, "w") as f:
        f.write(md)
    with open(os.path.join(run_dir, "gate.json"), "w") as f:
        json.dump({"pass": not failed, "failed": failed, "counts": counts, "rows": rows}, f, indent=2)
    return md


def main() -> int:
    repo = os.popen("git rev-parse --show-toplevel").read().strip() or "."
    ap = argparse.ArgumentParser(description="E2E KPI gate")
    ap.add_argument("run_dir")
    ap.add_argument("--thresholds", default=os.path.join(repo, "e2e", "kpi-thresholds.yaml"))
    args = ap.parse_args()

    with open(args.thresholds) as f:
        thresholds = yaml.safe_load(f)
    results = load_results(args.run_dir)
    rows, failed = evaluate(thresholds, results)
    md = write_report(args.run_dir, rows, failed)
    print(md)
    print(f"\n[gate] axes with results: {sorted(results)}")
    print(f"[gate] {'PASS' if not failed else 'FAIL: ' + ', '.join(failed)}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
