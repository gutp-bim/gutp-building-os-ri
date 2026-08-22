"""Regression tests for the perf/scale seeders' twin shape (#300).

The defect these guard is silent by construction: a seeder that emits bare `PointExt` nodes, or a
building typed `sbco:BuildingExt`, raises no error — the twin simply contains points the product
considers orphaned and a building it cannot see. So rather than assert on strings, these tests
re-implement the product's own reachability rule (`OxiGraphTwinAdminService.OrphanPattern`) over the
generated triples and require every seeded point to reach a `sbco:Building`.

Run with:
    python3 -m pytest Tools/e2e-performance/test_twin_hierarchy.py
"""

import os
import sys

sys.path.insert(0, os.path.dirname(__file__))

from seed_from_csv import build_insert as build_csv_insert  # noqa: E402
from seed_twin_points import (  # noqa: E402
    build_control_point_insert,
    build_insert as build_points_insert,
)
from twin_hierarchy import SBCO, TwinHierarchy  # noqa: E402


def parse(update: str) -> list[tuple[str, str, str]]:
    """Flatten a SPARQL INSERT DATA body into (subject, predicate, object) triples.

    A tiny Turtle-subset reader: enough for what these seeders emit (IRIs, quoted literals, bare
    booleans, `;` predicate-object lists, `.` terminators). Deliberately hand-rolled rather than
    pulling in rdflib — the perf tools have no third-party runtime dependency today, and adding one
    just for a regression test would change how the suite is installed.
    """
    body = update[update.index("{") + 1 : update.rindex("}")]

    tokens: list[str] = []
    i = 0
    while i < len(body):
        ch = body[i]
        if ch.isspace():
            i += 1
        elif ch == "<":
            end = body.index(">", i)
            tokens.append(body[i + 1 : end])
            i = end + 1
        elif ch == '"':
            j = i + 1
            buf = []
            while body[j] != '"':
                if body[j] == "\\":
                    buf.append(body[j + 1])
                    j += 2
                else:
                    buf.append(body[j])
                    j += 1
            tokens.append("".join(buf))
            i = j + 1
        elif ch in ";.":
            tokens.append(ch)
            i += 1
        else:  # bare token (a, true/false, numbers)
            j = i
            while j < len(body) and not body[j].isspace() and body[j] not in ';.<"':
                j += 1
            tokens.append(body[i:j])
            i = j

    triples: list[tuple[str, str, str]] = []
    subject = predicate = None
    expect = "subject"
    for token in tokens:
        if token == ".":
            subject = predicate = None
            expect = "subject"
        elif token == ";":
            expect = "predicate"
        elif expect == "subject":
            subject = token
            expect = "predicate"
        elif expect == "predicate":
            predicate = token
            expect = "object"
        else:
            triples.append((subject, predicate, token))
            expect = "done"  # next token must follow a ';' or '.'
    return triples


def reaches_building(triples: list[tuple[str, str, str]]) -> tuple[set[str], set[str]]:
    """Return (points, points that reach a Building), mirroring OrphanPattern's three chains."""
    typed: dict[str, set[str]] = {}
    out: dict[tuple[str, str], set[str]] = {}
    for s, p, o in triples:
        if p == "a":
            typed.setdefault(o, set()).add(s)
        else:
            out.setdefault((s, p), set()).add(o)

    points = typed.get(f"{SBCO}PointExt", set())
    buildings = typed.get(f"{SBCO}Building", set())
    levels = typed.get(f"{SBCO}Level", set())
    rooms = typed.get(f"{SBCO}Room", set())

    def obj(s: str, p: str) -> set[str]:
        return out.get((s, p), set())

    # Levels that a Building hasPart, and Rooms that such a Level hasPart.
    levels_under_building = {
        lv for b in buildings for lv in obj(b, f"{SBCO}hasPart") if lv in levels
    }
    rooms_under_building = {
        rm
        for lv in levels_under_building
        for rm in obj(lv, f"{SBCO}hasPart")
        if rm in rooms
    }
    level_names_under_building = {
        name for lv in levels_under_building for name in obj(lv, f"{SBCO}name")
    }

    connected = set()
    for device in {s for (s, p) in out if p == f"{SBCO}hasPoint"}:
        anchors = obj(device, f"{SBCO}locatedIn")
        floor_literals = obj(device, f"{SBCO}floor")
        anchored = (
            bool(anchors & rooms_under_building)  # chain A
            or bool(anchors & levels_under_building)  # chain B
            or bool(floor_literals & level_names_under_building)  # chain C
        )
        if anchored:
            connected |= obj(device, f"{SBCO}hasPoint")

    return points, points & connected


def assert_no_orphans(update: str) -> None:
    triples = parse(update)
    points, connected = reaches_building(triples)
    assert points, "expected the seeder to emit at least one PointExt"
    assert points == connected, (
        f"{len(points - connected)}/{len(points)} seeded points cannot reach a sbco:Building — "
        "the product would report them as orphans and strict ingress (#292) would drop their frames"
    )


class TestSeedTwinPoints:
    def test_load_generator_points_reach_a_building(self):
        ids = [f"perf-point-abcdef12-{d:05d}-{p:03d}" for d in range(2) for p in range(3)]
        assert_no_orphans(build_points_insert(ids))

    def test_control_point_reaches_a_building(self):
        assert_no_orphans(build_control_point_insert("perf-ctl-1", "GW-PERF"))

    def test_points_carry_the_building_literal_ingress_resolves_on(self):
        # IPointMetadataCache reads sbco:building to resolve the building per frame; without it the
        # twin is shaped correctly but telemetry still fails to enrich.
        update = build_points_insert(["perf-point-abcdef12-00000-000"])
        assert f'<{SBCO}building>' in update


class TestSeedFromCsv:
    def test_csv_seeded_points_reach_a_building(self):
        rows = [
            {
                "point_id": f"P{i}",
                "device_id": "D1",
                "device_name": "Dev",
                "device_type": "AHU",
                "point_name": f"Point {i}",
                "gateway_id": "GW-PERF",
                "writable": "false",
            }
            for i in range(3)
        ]
        assert_no_orphans(build_csv_insert(rows))


class TestTwinHierarchy:
    def test_building_is_typed_sbco_Building(self):
        # sbco:BuildingExt is not in the ontology: using it fails silently — the building simply
        # never appears in ListBuildings.
        triples = TwinHierarchy("test").triples()
        joined = "\n".join(triples)
        assert f"<{SBCO}Building>" in joined
        assert "BuildingExt" not in joined

    def test_chain_is_site_building_level_room(self):
        h = TwinHierarchy("test")
        triples = parse("INSERT DATA {\n" + "\n".join(h.triples()) + "\n}")
        has_part = {(s, o) for s, p, o in triples if p == f"{SBCO}hasPart"}
        assert (h.site_uri, h.building_uri) in has_part
        assert (h.building_uri, h.floor_uri) in has_part
        assert (h.floor_uri, h.room_uri) in has_part

    def test_distinct_buildings_get_distinct_uris(self):
        a = TwinHierarchy("test", building_id="B1")
        b = TwinHierarchy("test", building_id="B2")
        assert a.building_uri != b.building_uri
        assert a.room_uri != b.room_uri
