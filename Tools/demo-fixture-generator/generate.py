"""Demo dataset generator (#458).

Builds the `make demo` dataset — a ~1,200-point building with per-point expected intervals, alarm
thresholds, customTags and a deterministic stale / missing / alarm scenario — from the small,
hand-maintained `fixtures/demo/manifest.toml`, instead of hand-maintaining a 1,200-point twin.

    python3 Tools/demo-fixture-generator/generate.py           # rewrite fixtures/demo/*
    python3 Tools/demo-fixture-generator/generate.py --check   # exit 1 if the committed files drift

Outputs (all in fixtures/demo/, all generated — do not hand-edit):
  twin.ttl          SBCO Turtle seed: the demo building, then fixtures/e2e/twin.ttl verbatim so the
                    existing GW-SOS-001 / SOS-PT-001..008 control demo and its E2E keep working
  pointlist.csv     the demo building's point list, same 30-column header as fixtures/e2e/pointlist.csv
  feeder-plan.json  per-point stream parameters + scenario role, read by the demo feeder

Deterministic by construction: the manifest is walked in a fixed order, and the scenario picks use
`random.Random(seed)` over SORTED candidate lists, so neither dict ordering nor the order of the
tag list can change which points are picked. Standard library only (tomllib → Python 3.11+).
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import random
import sys
import tomllib
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEMO_DIR = REPO_ROOT / "fixtures" / "demo"
E2E_TWIN = REPO_ROOT / "fixtures" / "e2e" / "twin.ttl"

CSV_HEADER = [
    "gateway_id", "device_id", "device_name", "device_type", "site", "building", "floor",
    "installation_area", "target_area", "panel", "point_type", "point_specification", "point_id",
    "point_name", "writable", "interval", "unit", "max_pres_value", "min_pres_value", "labels",
    "states", "scale", "tags", "description", "supplier", "owner", "local_id", "device_id_bacnet",
    "object_type_bacnet", "instance_no_bacnet",
]

INCLUDED_MARKER = "# ── Included verbatim"


def load_manifest(path: Path) -> dict:
    with path.open("rb") as f:
        return tomllib.load(f)


# ── Model ──────────────────────────────────────────────────────────────────────
def _num(v) -> str:
    """Render a manifest number the way the twin stores it: integral values without a '.0'."""
    f = float(v)
    return str(int(f)) if f.is_integer() else repr(f)


def _ttl_str(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def build_model(m: dict) -> dict:
    """Expand the manifest into levels / rooms / equipment / points (pure, ordered)."""
    prefix = m["building"]["idPrefix"]
    eq_types = m["equipmentTypes"]
    templates = m["pointTemplates"]

    levels, rooms, equipment, points = [], [], [], []
    for floor in m["floors"]:
        fname = floor["name"]
        level_id = f"{prefix}-{fname}"
        gateway_id = f"GW-{prefix}-{fname}"
        levels.append({"id": level_id, "name": fname})
        floor_rooms = [
            {"id": f"{level_id}-R{i:02d}", "name": f"{fname}-{i:02d} 執務エリア", "level": level_id}
            for i in range(1, floor["rooms"] + 1)
        ]
        rooms.extend(floor_rooms)
        machine_room = None
        if any(eq_types[k]["placement"] == "machineRoom" and n > 0 for k, n in floor["equipment"].items()):
            machine_room = {"id": f"{level_id}-MR", "name": f"{fname} 機械室", "level": level_id}
            rooms.append(machine_room)

        # Walk equipment types in the manifest's declared type order (eq_types), not the floor
        # table's, so a floor listing them differently cannot reshuffle ids.
        room_cursor = 0
        for type_key, et in eq_types.items():
            count = floor["equipment"].get(type_key, 0)
            for n in range(1, count + 1):
                dev_id = f"{level_id}-{et['code']}{n:02d}"
                if et["placement"] == "room" and floor_rooms:
                    room = floor_rooms[room_cursor % len(floor_rooms)]
                    room_cursor += 1
                    located_in, area = room["id"], room["name"]
                elif et["placement"] == "machineRoom":
                    located_in, area = machine_room["id"], machine_room["name"]
                else:
                    located_in, area = level_id, fname
                dev = {
                    "id": dev_id, "name": f"{et['name']} {fname}-{n:02d}", "deviceType": et["deviceType"],
                    "level": level_id, "locatedIn": located_in, "area": area, "points": [],
                }
                equipment.append(dev)
                for t_key in et["points"]:
                    t = templates[t_key]
                    pid = f"{dev_id}-{t['code']}"
                    dev["points"].append(pid)
                    points.append({
                        "pointId": pid, "name": f"{dev['name']} {t['name']}", "gatewayId": gateway_id,
                        "floor": fname, "level": level_id, "equipment": type_key, "nth": n,
                        "device": dev, "template": t_key, "t": t, "tags": [],
                    })

    for tag in m.get("tags", []):
        for p in points:
            if _tag_matches(tag, p):
                p["tags"].append(tag["key"])
    for p in points:
        p["tags"].sort()

    return {"levels": levels, "rooms": rooms, "equipment": equipment, "points": points}


def _tag_matches(tag: dict, p: dict) -> bool:
    if "floor" in tag and p["floor"] != tag["floor"]:
        return False
    if "equipment" in tag and p["equipment"] != tag["equipment"]:
        return False
    if "template" in tag and p["template"] not in tag["template"]:
        return False
    if "everyNth" in tag and p["nth"] % tag["everyNth"] != 0:
        return False
    return True


def assign_roles(m: dict, points: list[dict]) -> list[str]:
    """Pick the degraded scenario's stale / missing / alarm points. Returns the down gateways."""
    sc = m["scenario"]
    down = sorted(sc.get("disconnectedGateways", []))
    rng = random.Random(m["seed"])
    for p in points:
        p["role"] = "normal"

    for role in ("stale", "missing", "alarm"):
        spec = sc[role]
        pool = sorted(
            p["pointId"] for p in points
            if p["role"] == "normal" and p["template"] in spec["templates"] and p["gatewayId"] not in down
        )
        if len(pool) < spec["count"]:
            raise ValueError(f"scenario.{role}: only {len(pool)} candidates for count={spec['count']}")
        chosen = set(rng.sample(pool, spec["count"]))
        for p in points:
            if p["pointId"] in chosen:
                p["role"] = role
                if role == "alarm":
                    p["alarmValue"] = float(spec["value"])
    return down


# ── Renderers ──────────────────────────────────────────────────────────────────
def render_twin(m: dict, model: dict, e2e_twin: str) -> str:
    site, bldg = m["site"], m["building"]
    out = io.StringIO()
    w = out.write
    w("# =============================================================================\n")
    w(f"# GENERATED by Tools/demo-fixture-generator/generate.py from fixtures/demo/manifest.toml (#458).\n")
    w("# Do not edit by hand — edit the manifest and regenerate (CI checks for drift).\n")
    w(f"# {bldg['name']}: {len(model['levels'])} levels / {len(model['rooms'])} rooms / "
      f"{len(model['equipment'])} devices / {len(model['points'])} points\n")
    w("# =============================================================================\n\n")
    w("@prefix sbco: <https://www.sbco.or.jp/ont/> .\n")
    w("@prefix sbr:  <https://www.sbco.or.jp/ont/resource/> .\n")
    w("@prefix bos:  <http://buildingos.gutp.jp/ontology#> .\n")
    w("@prefix xsd:  <http://www.w3.org/2001/XMLSchema#> .\n\n")

    w(f"sbr:{site['id']} a sbco:Site ;\n  sbco:id {_ttl_str(site['id'])} ;\n"
      f"  sbco:name {_ttl_str(site['name'])} ;\n  sbco:hasPart sbr:{bldg['id']} .\n\n")
    w(f"sbr:{bldg['id']} a sbco:Building ;\n  sbco:id {_ttl_str(bldg['id'])} ;\n"
      f"  sbco:name {_ttl_str(bldg['name'])}")
    for lv in model["levels"]:
        w(f" ;\n  sbco:hasPart sbr:{lv['id']}")
    w(" .\n\n")

    for lv in model["levels"]:
        w(f"sbr:{lv['id']} a sbco:Level ;\n  sbco:id {_ttl_str(lv['id'])} ;\n  sbco:name {_ttl_str(lv['name'])}")
        for r in model["rooms"]:
            if r["level"] == lv["id"]:
                w(f" ;\n  sbco:hasPart sbr:{r['id']}")
        w(" .\n\n")
    for r in model["rooms"]:
        w(f"sbr:{r['id']} a sbco:Room ;\n  sbco:id {_ttl_str(r['id'])} ;\n  sbco:name {_ttl_str(r['name'])} .\n\n")

    for d in model["equipment"]:
        w(f"sbr:{d['id']} a sbco:EquipmentExt ;\n  sbco:id {_ttl_str(d['id'])} ;\n"
          f"  sbco:name {_ttl_str(d['name'])} ;\n  sbco:deviceType {_ttl_str(d['deviceType'])} ;\n"
          f"  sbco:floor {_ttl_str(d['level'])} ;\n  sbco:locatedIn sbr:{d['locatedIn']}")
        for pid in d["points"]:
            w(f" ;\n  sbco:hasPoint sbr:{pid}")
        w(" .\n\n")

    for p in model["points"]:
        t = p["t"]
        lines = [
            f"sbr:{p['pointId']} a sbco:PointExt ;",
            f"  sbco:id {_ttl_str(p['pointId'])} ;",
            f"  sbco:name {_ttl_str(p['name'])} ;",
            f"  sbco:gatewayId {_ttl_str(p['gatewayId'])} ;",
            f"  sbco:building {_ttl_str(bldg['id'])} ;",
            f"  sbco:floor {_ttl_str(p['level'])} ;",
            f"  sbco:pointType {_ttl_str(t['pointType'])} ;",
            f"  sbco:pointSpecification {_ttl_str(t['spec'])} ;",
        ]
        if "unit" in t:
            lines.append(f"  sbco:unit {_ttl_str(t['unit'])} ;")
        lines += [
            f"  sbco:writable {_ttl_str('true' if t.get('writable') else 'false')} ;",
            f"  sbco:interval {_ttl_str(_num(t['interval']))} ;",
            f"  sbco:localId {_ttl_str('ns=2;s=' + p['pointId'])} ;",
            f"  bos:dataType {_ttl_str(t['kind'])} ;",
        ]
        for key in ("minValue", "maxValue", "alarmHigh", "warnHigh", "warnLow", "alarmLow"):
            if key in t:
                lines.append(f"  bos:{key} {_ttl_str(_num(t[key]))} ;")
        if "enumLabels" in t:
            labels = json.dumps({k: t["enumLabels"][k] for k in sorted(t["enumLabels"], key=int)},
                                ensure_ascii=False, separators=(",", ":"))
            lines.append(f"  bos:enumLabels {_ttl_str(labels)} ;")
        for tag in p["tags"]:
            lines.append(f"  sbco:customTags [ a sbco:KeyBoolMapEntry ; sbco:key {_ttl_str(tag)} ; "
                         f'sbco:value "true"^^xsd:boolean ] ;')
        lines[-1] = lines[-1][:-2] + " ."
        w("\n".join(lines) + "\n\n")

    w(f"{INCLUDED_MARKER} from fixtures/e2e/twin.ttl (GW-SOS-001 / SOS-PT-001..008) ──────────\n")
    w("# Kept so the control demo, `make demo-e2e` and the demo-smoke workflow keep their points.\n\n")
    w(e2e_twin)
    return out.getvalue()


def render_csv(m: dict, model: dict) -> str:
    out = io.StringIO()
    wr = csv.writer(out, lineterminator="\n")
    wr.writerow(CSV_HEADER)
    for p in model["points"]:
        t, d = p["t"], p["device"]
        bounded = "lo" in t and "hi" in t and t["kind"] == "number"
        labels = [t["enumLabels"][k] for k in sorted(t["enumLabels"], key=int)] if "enumLabels" in t else []
        wr.writerow([
            p["gatewayId"], d["id"], d["name"], d["deviceType"], m["site"]["id"], m["building"]["id"],
            p["level"], d["area"], d["area"], "", t["pointType"], t["spec"], p["pointId"], p["name"],
            "true" if t.get("writable") else "false", _num(t["interval"]), t.get("unit", ""),
            _num(t["hi"]) if bounded else "", _num(t["lo"]) if bounded else "",
            "&&".join(labels), "|".join(labels), "1.0", "&&".join(p["tags"]), "", "", "",
            "ns=2;s=" + p["pointId"], "", "", "",
        ])
    return out.getvalue()


def render_plan(model: dict, down: list[str]) -> str:
    rows = []
    for p in model["points"]:
        t = p["t"]
        row = {
            "pointId": p["pointId"], "gatewayId": p["gatewayId"], "floor": p["floor"],
            "equipment": p["equipment"], "template": p["template"], "kind": t["kind"],
            "interval": t["interval"], "base": t["base"], "amplitude": t["amplitude"],
            "period": t["period"], "lo": t.get("lo", 0), "hi": t.get("hi", 0),
            "role": p["role"], "tags": p["tags"],
        }
        if p["role"] == "alarm":
            row["alarmValue"] = p["alarmValue"]
            row["alarmHigh"] = t["alarmHigh"]
        rows.append(json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
    gateways = sorted({p["gatewayId"] for p in model["points"]})
    header = json.dumps({"disconnectedGateways": down, "gateways": gateways},
                        ensure_ascii=False, sort_keys=True, separators=(",", ":"))[:-1]
    return header + ',"points":[\n' + ",\n".join(rows) + "\n]}\n"


def generate(m: dict, e2e_twin: str) -> dict[str, str]:
    model = build_model(m)
    down = assign_roles(m, model["points"])
    return {
        "twin.ttl": render_twin(m, model, e2e_twin),
        "pointlist.csv": render_csv(m, model),
        "feeder-plan.json": render_plan(model, down),
    }


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true", help="fail if committed files differ from the manifest")
    args = ap.parse_args(argv)

    out = generate(load_manifest(DEMO_DIR / "manifest.toml"), E2E_TWIN.read_text(encoding="utf-8"))
    stale = []
    for name, content in out.items():
        path = DEMO_DIR / name
        current = path.read_text(encoding="utf-8") if path.exists() else None
        if current == content:
            continue
        if args.check:
            stale.append(name)
        else:
            path.write_text(content, encoding="utf-8")
            print(f"wrote {path.relative_to(REPO_ROOT)}")
    if stale:
        print("fixtures/demo is out of date with manifest.toml: " + ", ".join(stale), file=sys.stderr)
        print("Run: python3 Tools/demo-fixture-generator/generate.py", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
