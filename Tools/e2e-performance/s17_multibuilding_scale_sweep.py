#!/usr/bin/env python3
"""Multi-building 2k→50k Point evaluation coordinator (#261).

The command executes one stage runner per scale. The runner receives deterministic topology as JSON
and must write the measured stage JSON to the supplied output path. This coordinator validates every
KPI, writes ``kpi-summary.json`` and ``report.md``, and returns the 1-based failed stage as its exit
code (0 means all stages passed).
"""

from __future__ import annotations

import argparse
import json
import os
import shlex
import subprocess
import sys
from collections import Counter
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path


DEFAULT_SCALES = (2_000, 5_000, 10_000, 50_000)


@dataclass(frozen=True)
class Point:
    point_id: str
    building_id: str
    gateway_id: str


@dataclass(frozen=True)
class Thresholds:
    point_list_ms: float = 5_000
    loss_rate: float = 0.01
    flush_ms: float = 120_000


def build_topology(scale: int, buildings: int, gateways: int, run_id: str) -> list[Point]:
    if scale < 1 or buildings < 1 or gateways < 1:
        raise ValueError("scale, buildings, and gateways must be positive")
    if gateways < buildings:
        raise ValueError("gateways must be greater than or equal to buildings")
    tag = "".join(ch for ch in run_id.lower() if ch.isalnum())[-12:] or "run"
    return [
        Point(
            point_id=f"s17-{tag}-p{index:05d}",
            building_id=f"s17-{tag}-b{index % buildings:03d}",
            gateway_id=f"GW-S17-{tag.upper()}-{index % gateways:03d}",
        )
        for index in range(scale)
    ]


def count_by(points: list[Point], field: str) -> Counter:
    return Counter(getattr(point, field) for point in points)


def evaluate_stage(*, scale: int, point_list_ms: float, accepted: int, rejected: int,
                   expected_accepted: int, expected_rejected: int, lake_rows: int,
                   flush_ms: float, thresholds: Thresholds) -> dict:
    loss = max(0, expected_accepted - lake_rows)
    loss_rate = loss / expected_accepted if expected_accepted else 0.0
    failures = []
    if point_list_ms > thresholds.point_list_ms:
        failures.append("point_list_ms")
    if loss_rate > thresholds.loss_rate or accepted != expected_accepted:
        failures.append("loss_rate")
    if flush_ms > thresholds.flush_ms:
        failures.append("flush_ms")
    if rejected != expected_rejected:
        failures.append("rejected")
    metrics = {
        "point_count": scale,
        "point_list_ms": round(point_list_ms, 3),
        "accepted": accepted,
        "rejected": rejected,
        "expected_accepted": expected_accepted,
        "expected_rejected": expected_rejected,
        "lake_rows": lake_rows,
        "loss": loss,
        "loss_rate": round(loss_rate, 6),
        "parquet_flush_ms": round(flush_ms, 3),
    }
    return {"scale": scale, "metrics": metrics, "passed": not failures,
            "exceeded_thresholds": failures}


def passing_result(scale: int) -> dict:
    return evaluate_stage(scale=scale, point_list_ms=100, accepted=scale, rejected=10,
                          expected_accepted=scale, expected_rejected=10, lake_rows=scale,
                          flush_ms=1_000, thresholds=Thresholds())


def failure_exit_code(results: list[dict]) -> int:
    return next((index for index, result in enumerate(results, 1) if not result["passed"]), 0)


def render_markdown(results: list[dict], run_id: str) -> str:
    passing = [result["scale"] for result in results if result["passed"]]
    largest = max(passing) if passing else 0
    lines = [
        "# Multi-building scale sweep (#261)", "", f"Run: `{run_id}`", "",
        f"実測済み最大規模: **{largest:,} Point**", "",
        "| Point | status | Point List ms | accepted/rejected | loss | Parquet flush ms | exceeded |",
        "|--:|:--|--:|:--|--:|--:|:--|",
    ]
    for result in results:
        metric = result["metrics"]
        exceeded = ", ".join(result["exceeded_thresholds"]) or "—"
        lines.append(
            f"| {result['scale']:,} | {'PASS' if result['passed'] else 'FAIL'} | "
            f"{metric.get('point_list_ms', '—')} | "
            f"{metric.get('accepted', '—')}/{metric.get('rejected', '—')} | "
            f"{metric.get('loss', '—')} "
            f"({metric.get('loss_rate', 0):.2%}) | "
            f"{metric.get('parquet_flush_ms', '—')} | {exceeded} |"
        )
    return "\n".join(lines) + "\n"


def _evaluate_measurement(measurement: dict, scale: int, thresholds: Thresholds) -> dict:
    required = ("point_list_ms", "accepted", "rejected", "expected_accepted",
                "expected_rejected", "lake_rows", "flush_ms")
    missing = [key for key in required if key not in measurement]
    if missing:
        raise ValueError(f"stage {scale} missing metrics: {', '.join(missing)}")
    return evaluate_stage(scale=scale, thresholds=thresholds,
                          **{key: measurement[key] for key in required})


def run(args: argparse.Namespace) -> int:
    run_id = args.run_id or datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-s17")
    output = Path(args.output) / run_id
    output.mkdir(parents=True, exist_ok=True)
    thresholds = Thresholds(args.max_point_list_ms, args.max_loss_rate, args.max_flush_ms)
    results = []
    for stage_index, scale in enumerate(args.scales, 1):
        stage = output / f"stage-{scale}"
        stage.mkdir(exist_ok=True)
        buildings = min(args.buildings, scale)
        gateways = min(max(args.gateways, buildings), scale)
        topology_path = stage / "topology.json"
        measurement_path = stage / "measurements.json"
        topology = build_topology(scale, buildings, gateways, f"{run_id}-{scale}")
        topology_path.write_text(json.dumps([asdict(point) for point in topology], indent=2))
        command = args.stage_command.format(
            scale=scale, buildings=buildings, gateways=gateways,
            topology=shlex.quote(str(topology_path)), output=shlex.quote(str(measurement_path)),
            run_id=shlex.quote(f"{run_id}-{scale}"),
        )
        completed = subprocess.run(shlex.split(command), check=False, env=os.environ.copy())
        if completed.returncode != 0 or not measurement_path.exists():
            result = {"scale": scale, "metrics": {}, "passed": False,
                      "exceeded_thresholds": ["stage_runner"],
                      "runner_exit_code": completed.returncode}
        else:
            result = _evaluate_measurement(json.loads(measurement_path.read_text()), scale, thresholds)
        results.append(result)
        (stage / "kpi.json").write_text(json.dumps(result, indent=2))
        if not result["passed"] and not args.continue_on_failure:
            break

    summary = {
        "run_id": run_id,
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "thresholds": asdict(thresholds),
        "results": results,
        "largest_verified_scale": max((r["scale"] for r in results if r["passed"]), default=0),
    }
    (output / "kpi-summary.json").write_text(json.dumps(summary, indent=2))
    (output / "report.md").write_text(render_markdown(results, run_id))
    return failure_exit_code(results)


def create_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    stage_runner = Path(__file__).with_name("s17_scale_stage.py")
    parser.add_argument("--stage-command",
                        default=f"{shlex.quote(sys.executable)} {shlex.quote(str(stage_runner))} "
                                "--topology {topology} --output {output} --run-id {run_id}",
                        help="command template; supports {scale}, {buildings}, {gateways}, "
                             "{topology}, {output}, and {run_id}")
    parser.add_argument("--scales", nargs="+", type=int, default=list(DEFAULT_SCALES))
    parser.add_argument("--buildings", type=int, default=10)
    parser.add_argument("--gateways", type=int, default=20)
    parser.add_argument("--run-id")
    parser.add_argument("--output", default=str(Path(__file__).parent / "results"))
    parser.add_argument("--max-point-list-ms", type=float, default=5_000)
    parser.add_argument("--max-loss-rate", type=float, default=0.01)
    parser.add_argument("--max-flush-ms", type=float, default=120_000)
    parser.add_argument("--continue-on-failure", action="store_true")
    return parser


def main() -> int:
    return run(create_parser().parse_args())


if __name__ == "__main__":
    sys.exit(main())
