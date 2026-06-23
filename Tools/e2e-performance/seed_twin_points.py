#!/usr/bin/env python3
"""Seed the OxiGraph digital twin with the load generator's synthetic points (S5 prep).

The API read path (`/telemetries/query`, `/telemetries/warm`) resolves/authorizes a point via the
twin (`digitalTwinDatabase.GetPoint` → `?pt a sbco:PointExt ; sbco:id "<id>" ; sbco:name "..."`) and
returns 404 for unknown points. The load generator emits synthetic `perf-point-...` ids that are not in
the twin, so warm/range/query reads 404 for them. This inserts matching `sbco:PointExt` nodes via
SPARQL `INSERT DATA` (additive — does not wipe the seeded sample twin) so S5 can read real lake data.

Point ids mirror device_load_generator naming: perf-point-{run8}-{dev:05d}-{pt:03d}.

Usage:
  python seed_twin_points.py --run-id <RUN_ID> [--devices 10 --points-per-device 10]
                             [--oxigraph http://localhost:7878]
"""

from __future__ import annotations

import argparse
import logging
import os
import sys
import urllib.request

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)

SBCO = "https://www.sbco.or.jp/ont/"
DEFAULT_OXIGRAPH = os.environ.get("OXIGRAPH_URL", "http://localhost:7878")


def point_ids(run_id: str, devices: int, points_per_device: int) -> list[str]:
    run8 = run_id[:8]
    return [
        f"perf-point-{run8}-{d:05d}-{p:03d}"
        for d in range(devices)
        for p in range(points_per_device)
    ]


def build_insert(ids: list[str]) -> str:
    triples = "\n".join(
        f'  <urn:perf:pt:{pid}> a <{SBCO}PointExt> ; '
        f'<{SBCO}id> "{pid}" ; <{SBCO}name> "{pid}" ; <{SBCO}writable> false .'
        for pid in ids
    )
    return f"INSERT DATA {{\n{triples}\n}}"


def build_control_point_insert(point_id: str, gateway_id: str) -> str:
    """A writable, controllable point for S6: PointExt(writable, gatewayId) + an EquipmentExt that
    hasPoint it. GetPointDetailByPointId requires the point reachable from an EquipmentExt via hasPoint,
    and the control path needs writable=true + a gatewayId (its binding resolves the egress ControlType)."""
    pt = f"urn:perf:ctlpt:{point_id}"
    dev = f"urn:perf:ctldev:{point_id}"
    return (
        f"INSERT DATA {{\n"
        f'  <{pt}> a <{SBCO}PointExt> ; <{SBCO}id> "{point_id}" ; <{SBCO}name> "{point_id}" ; '
        f'<{SBCO}writable> true ; <{SBCO}gatewayId> "{gateway_id}" .\n'
        f'  <{dev}> a <{SBCO}EquipmentExt> ; <{SBCO}id> "DEV-{point_id}" ; <{SBCO}name> "Device {point_id}" ; '
        f"<{SBCO}hasPoint> <{pt}> .\n"
        f"}}"
    )


def post_update(oxigraph: str, update: str) -> None:
    req = urllib.request.Request(
        f"{oxigraph.rstrip('/')}/update",
        data=update.encode("utf-8"),
        headers={"Content-Type": "application/sparql-update"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=30) as resp:  # noqa: S310 (local dev endpoint)
        if resp.status not in (200, 204):
            raise RuntimeError(f"OxiGraph update returned {resp.status}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Seed synthetic points into the OxiGraph twin")
    parser.add_argument("--run-id", help="Load-gen run id (its run_id[:8] is embedded in point ids)")
    parser.add_argument("--devices", type=int, default=10)
    parser.add_argument("--points-per-device", type=int, default=10)
    parser.add_argument("--control-point", help="Seed a single writable/controllable point (S6) with this id")
    parser.add_argument("--gateway", default="GW-PERF", help="gatewayId for the control point (S6)")
    parser.add_argument("--oxigraph", default=DEFAULT_OXIGRAPH)
    args = parser.parse_args()

    if args.control_point:
        logger.info("Seeding controllable point %s (gateway=%s) into %s",
                    args.control_point, args.gateway, args.oxigraph)
        post_update(args.oxigraph, build_control_point_insert(args.control_point, args.gateway))
        print(args.control_point)
        return

    if not args.run_id:
        parser.error("--run-id is required unless --control-point is given")

    ids = point_ids(args.run_id, args.devices, args.points_per_device)
    logger.info("Seeding %d PointExt nodes into %s (run8=%s)", len(ids), args.oxigraph, args.run_id[:8])
    post_update(args.oxigraph, build_insert(ids))
    logger.info("Seeded %d points. Sample: %s", len(ids), ", ".join(ids[:3]))
    # Emit the point ids (comma-separated) to stdout so a runner can pass them to k6 as POINT_IDS.
    print(",".join(ids))


if __name__ == "__main__":
    sys.exit(main())
