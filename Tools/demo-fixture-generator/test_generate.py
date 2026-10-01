"""Tests for the demo fixture generator (#458).

Standard library + pytest only. Run with:
    python3 -m pytest Tools/demo-fixture-generator
"""

from __future__ import annotations

import csv
import io
import json
import os
import re
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, os.path.dirname(__file__))

from generate import REPO_ROOT, generate, load_manifest  # noqa: E402

DEMO_DIR = REPO_ROOT / "fixtures" / "demo"
E2E_TWIN = REPO_ROOT / "fixtures" / "e2e" / "twin.ttl"
E2E_CSV = REPO_ROOT / "fixtures" / "e2e" / "pointlist.csv"


def _out():
    return generate(load_manifest(DEMO_DIR / "manifest.toml"), E2E_TWIN.read_text(encoding="utf-8"))


def _plan(out):
    return json.loads(out["feeder-plan.json"])


def _demo_points(out):
    return _plan(out)["points"]


def test_generation_is_byte_for_byte_deterministic():
    assert _out() == _out()


def test_committed_files_match_the_manifest():
    # The CI drift gate in miniature: a manifest edit without regenerating fails here first.
    for name, content in _out().items():
        committed = (DEMO_DIR / name).read_text(encoding="utf-8")
        assert committed == content, f"fixtures/demo/{name} is stale — run generate.py"


def test_roughly_1200_points_across_three_floors():
    points = _demo_points(_out())
    assert 1100 <= len(points) <= 1300
    floors = Counter(p["floor"] for p in points)
    assert set(floors) >= {"1F", "2F", "3F"}


def test_point_ids_are_unique_and_disjoint_from_the_e2e_building():
    ids = [p["pointId"] for p in _demo_points(_out())]
    assert len(ids) == len(set(ids))
    assert not any(i.startswith("SOS-PT-") for i in ids)


def test_every_demo_point_has_an_interval_in_the_twin():
    ttl = _out()["twin.ttl"]
    demo_section = ttl.split("# ── Included verbatim")[0]
    point_blocks = re.findall(r"a sbco:PointExt ;(.*?) \.\n", demo_section, flags=re.S)
    assert len(point_blocks) == len(_demo_points(_out()))
    assert all("sbco:interval" in block for block in point_blocks)


def test_some_points_carry_alarm_thresholds():
    ttl = _out()["twin.ttl"]
    assert ttl.count("bos:alarmHigh") > 10


def test_twin_includes_the_e2e_building_verbatim():
    ttl = _out()["twin.ttl"]
    assert E2E_TWIN.read_text(encoding="utf-8") in ttl


def test_every_gateway_belongs_to_the_demo_building_only():
    points = _demo_points(_out())
    assert {p["gatewayId"] for p in points} == {"GW-DEMO-1F", "GW-DEMO-2F", "GW-DEMO-3F", "GW-DEMO-RF"}
    # gateway_id must be globally unique across buildings (OxiGraphSeedHostedService fails startup).
    assert "GW-SOS-001" not in {p["gatewayId"] for p in points}


def test_tags_are_operational_not_structural():
    ttl = _out()["twin.ttl"]
    keys = set(re.findall(r'sbco:key "([^"]+)"', ttl))
    assert keys == {"critical", "tenant-a", "energy-saving-target", "maintenance-required"}
    assert not any("=" in k for k in keys)


def test_tag_selectors():
    points = _demo_points(_out())
    by_tag = {}
    for p in points:
        for t in p["tags"]:
            by_tag.setdefault(t, []).append(p)
    assert {p["template"] for p in by_tag["critical"]} == {"supply_temp"}
    assert {p["floor"] for p in by_tag["tenant-a"]} == {"2F"}
    assert len(by_tag["tenant-a"]) == sum(1 for p in points if p["floor"] == "2F")
    # everyNth = 5 over 15 FCUs per floor → FCU05/10/15 × 4 points × 3 floors
    assert len(by_tag["maintenance-required"]) == 3 * 3 * 4


def test_degraded_scenario_counts():
    plan = _plan(_out())
    roles = Counter(p["role"] for p in plan["points"])
    assert roles["stale"] == 18
    assert roles["alarm"] == 2
    disconnected = set(plan["disconnectedGateways"])
    assert disconnected == {"GW-DEMO-RF"}
    behind_down_gateway = [p for p in plan["points"] if p["gatewayId"] in disconnected]
    assert len(behind_down_gateway) == 2
    # 3 never-received + 2 behind the down gateway = 5 missing on /home
    assert roles["missing"] + len(behind_down_gateway) == 5


def test_scenario_points_come_from_their_template_pools():
    points = _demo_points(_out())
    disconnected = set(_plan(_out())["disconnectedGateways"])
    for p in points:
        if p["role"] == "stale":
            assert p["template"] in {"fan_status", "zone_temp", "room_temp"}
            assert p["interval"] <= 60
        if p["role"] == "missing":
            assert p["template"] in {"co2", "room_humidity"}
        if p["role"] in ("stale", "missing", "alarm"):
            assert p["gatewayId"] not in disconnected


def test_alarm_points_stream_past_their_alarm_high():
    for p in _demo_points(_out()):
        if p["role"] == "alarm":
            assert p["alarmValue"] >= p["alarmHigh"]


def test_pointlist_csv_uses_the_standard_header_and_covers_every_point():
    out = _out()
    with E2E_CSV.open(encoding="utf-8") as f:
        e2e_header = next(csv.reader(f))
    rows = list(csv.reader(io.StringIO(out["pointlist.csv"])))
    assert rows[0] == e2e_header
    assert len(rows) - 1 == len(_demo_points(out))


def test_writable_points_carry_a_control_schema():
    ttl = _out()["twin.ttl"].split("# ── Included verbatim")[0]
    blocks = re.findall(r"a sbco:PointExt ;(.*?) \.\n", ttl, flags=re.S)
    writable = [b for b in blocks if 'sbco:writable "true"' in b]
    assert writable
    for b in writable:
        assert "bos:dataType" in b
        assert ("bos:minValue" in b and "bos:maxValue" in b) or "bos:enumLabels" in b


def test_rng_selection_is_independent_of_dict_ordering(tmp_path: Path):
    # Reordering the manifest's tag list must not change which points the scenario picks.
    manifest = load_manifest(DEMO_DIR / "manifest.toml")
    manifest["tags"] = list(reversed(manifest["tags"]))
    reordered = generate(manifest, E2E_TWIN.read_text(encoding="utf-8"))
    pick = lambda out: sorted((p["pointId"], p["role"]) for p in _demo_points(out) if p["role"] != "normal")  # noqa: E731
    assert pick(reordered) == pick(_out())
