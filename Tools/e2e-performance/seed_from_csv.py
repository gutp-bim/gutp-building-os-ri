#!/usr/bin/env python3
"""Seed the OxiGraph digital twin from the nexus-gateway point_list.csv.

The CSV is the authoritative source for gateway's point list.
This script imports all points as sbco:PointExt nodes so that:
  - GET /gateways/{gatewayId}/pointlist returns the correct points
  - GatewayIngress (gRPC) can resolve point_id → building/device/name

Usage:
  python seed_from_csv.py --csv path/to/point_list.csv [--oxigraph http://localhost:7878]
  python seed_from_csv.py --csv path/to/point_list.csv --cleanup   # delete seeded points
"""

from __future__ import annotations

import argparse
import csv
import logging
import os
import sys
import urllib.request

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)

SBCO = "https://www.sbco.or.jp/ont/"
BOS = "http://buildingos.gutp.jp/ontology#"

PT_URI_FMT = "urn:nexus:pt:{}"
DEV_URI_FMT = "urn:nexus:dev:{}"


def _esc(s: str) -> str:
    return s.replace("\\", "\\\\").replace('"', '\\"')


def load_csv(path: str) -> list[dict]:
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def build_delete_conflicts(point_ids: list[str]) -> str:
    """sbco:id が一致する既存 PointExt ノードを URI 問わず削除する（サンプルデータとの競合解消用）."""
    id_vals = " ".join(f'"{_esc(pid)}"' for pid in point_ids)
    return (
        "DELETE { ?pt ?p ?o }\n"
        "WHERE {\n"
        f"  VALUES ?id {{ {id_vals} }}\n"
        f"  ?pt a <{SBCO}PointExt> ; <{SBCO}id> ?id ; ?p ?o .\n"
        "}"
    )


def build_insert(rows: list[dict]) -> str:
    triples: list[str] = []
    devices: dict[str, dict] = {}

    for row in rows:
        pid = row["point_id"].strip()
        pt_uri = PT_URI_FMT.format(pid)
        dev_id = row["device_id"].strip()

        if dev_id not in devices:
            devices[dev_id] = {
                "name": row["device_name"].strip(),
                "type": row["device_type"].strip(),
                "points": [],
            }
        devices[dev_id]["points"].append(pt_uri)

        props: list[str] = [
            f'<{SBCO}id> "{_esc(pid)}"',
            f'<{SBCO}name> "{_esc(row["point_name"].strip())}"',
            f'<{SBCO}gatewayId> "{_esc(row["gateway_id"].strip())}"',
            f'<{SBCO}writable> {row["writable"].strip().lower()}',
        ]
        if row.get("interval", "").strip():
            props.append(f'<{SBCO}interval> "{_esc(row["interval"].strip())}"')
        if row.get("unit", "").strip():
            props.append(f'<{SBCO}unit> "{_esc(row["unit"].strip())}"')
        if row.get("max_pres_value", "").strip():
            props.append(f'<{BOS}maxValue> "{_esc(row["max_pres_value"].strip())}"')
        if row.get("min_pres_value", "").strip():
            props.append(f'<{BOS}minValue> "{_esc(row["min_pres_value"].strip())}"')
        if row.get("local_id", "").strip():
            props.append(f'<{SBCO}localId> "{_esc(row["local_id"].strip())}"')
        if row.get("device_id_bacnet", "").strip():
            props.append(f'<{SBCO}deviceIdBacnet> "{_esc(row["device_id_bacnet"].strip())}"')
        if row.get("instance_no_bacnet", "").strip():
            props.append(f'<{SBCO}instanceNoBacnet> "{_esc(row["instance_no_bacnet"].strip())}"')
        if row.get("object_type_bacnet", "").strip():
            props.append(f'<{SBCO}objectTypeBacnet> "{_esc(row["object_type_bacnet"].strip())}"')
        if row.get("point_type", "").strip():
            props.append(f'<{SBCO}pointType> "{_esc(row["point_type"].strip())}"')
        if row.get("point_specification", "").strip():
            props.append(f'<{SBCO}pointSpecification> "{_esc(row["point_specification"].strip())}"')
        if row.get("scale", "").strip():
            props.append(f'<{SBCO}scale> "{_esc(row["scale"].strip())}"')

        body = " ;\n    ".join(props)
        triples.append(f"  <{pt_uri}> a <{SBCO}PointExt> ;\n    {body} .")

    for dev_id, info in devices.items():
        dev_uri = DEV_URI_FMT.format(dev_id)
        props = [
            f'<{SBCO}id> "{_esc(dev_id)}"',
            f'<{SBCO}name> "{_esc(info["name"])}"',
        ]
        if info["type"]:
            props.append(f'<{SBCO}deviceType> "{_esc(info["type"])}"')
        for pt_uri in info["points"]:
            props.append(f"<{SBCO}hasPoint> <{pt_uri}>")
        body = " ;\n    ".join(props)
        triples.append(f"  <{dev_uri}> a <{SBCO}EquipmentExt> ;\n    {body} .")

    return "INSERT DATA {\n" + "\n\n".join(triples) + "\n}"


def build_delete(point_ids: list[str], device_ids: list[str]) -> str:
    parts: list[str] = []
    for pid in point_ids:
        parts.append(f"DELETE WHERE {{ <{PT_URI_FMT.format(pid)}> ?p ?o }}")
    for did in device_ids:
        parts.append(f"DELETE WHERE {{ <{DEV_URI_FMT.format(did)}> ?p ?o }}")
    return ";\n".join(parts)


def sparql_update(oxigraph: str, update: str) -> None:
    req = urllib.request.Request(
        f"{oxigraph.rstrip('/')}/update",
        data=update.encode("utf-8"),
        headers={"Content-Type": "application/sparql-update"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=30) as resp:  # noqa: S310
        if resp.status not in (200, 204):
            raise RuntimeError(f"OxiGraph update returned {resp.status}")


def main() -> int:
    ap = argparse.ArgumentParser(description="Seed OxiGraph from nexus-gateway point_list.csv")
    ap.add_argument("--csv", required=True, help="Path to point_list.csv")
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--cleanup", action="store_true", help="Delete the seeded points/devices")
    args = ap.parse_args()

    rows = load_csv(args.csv)
    point_ids = [r["point_id"].strip() for r in rows]
    device_ids = list(dict.fromkeys(r["device_id"].strip() for r in rows))
    gateway_ids = list(dict.fromkeys(r["gateway_id"].strip() for r in rows))

    if args.cleanup:
        logger.info("Deleting %d points and %d devices from %s", len(point_ids), len(device_ids), args.oxigraph)
        sparql_update(args.oxigraph, build_delete(point_ids, device_ids))
        logger.info("Cleanup done")
        return 0

    logger.info(
        "Seeding %d points / %d devices / gateways=%s into %s",
        len(rows), len(device_ids), gateway_ids, args.oxigraph,
    )
    logger.info("Deleting conflicting PointExt nodes with same sbco:id (sample data の重複解消)...")
    sparql_update(args.oxigraph, build_delete_conflicts(point_ids))
    sparql_update(args.oxigraph, build_insert(rows))
    logger.info("Seeded: %s", ", ".join(point_ids[:6]) + ("..." if len(point_ids) > 6 else ""))
    print(",".join(point_ids))
    return 0


if __name__ == "__main__":
    sys.exit(main())
