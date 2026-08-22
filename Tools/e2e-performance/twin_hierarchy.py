#!/usr/bin/env python3
"""Minimal spatial hierarchy for the perf/scale twin seeders (#300).

The seeders here are **test fixture generators**, not a CSV→RDF converter — the canonical converter
is the external `smartbuilding_datamodel_builder`. What is required of them is not faithful column
mapping but producing a twin the product itself considers valid: without a spatial chain, every
seeded point is an orphan by `OxiGraphTwinAdminService.OrphanPattern`'s definition, and the
`GRPC_INGRESS_REQUIRE_HIERARCHY` policy (#292) would discard every frame once it is turned on. A
measurement taken against a shape the product treats as broken is not measuring the product.

Reachability, quoted from `OrphanPattern` — a point is connected when any of these reaches a Building:

  A. Building -hasPart-> Level -hasPart-> Room <-locatedIn- EquipmentExt -hasPoint-> PointExt
  B. Building -hasPart-> Level <-locatedIn- EquipmentExt -hasPoint-> PointExt
  C. the EquipmentExt's `sbco:floor` literal matched against a Level's `sbco:name`

We emit chain A (the fullest of the three), so the seeded twin also exercises the Room hop that a
real building has and that hierarchy-traversing queries pay for.

Note the class is `sbco:Building`, not `sbco:BuildingExt` — the latter is not in the ontology
(`OxiGraphOntology.Cls_Building`), and using it fails silently: no error, the building simply never
appears in `ListBuildings` or the resource tree.
"""

from __future__ import annotations

SBCO = "https://www.sbco.or.jp/ont/"


def _esc(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


class TwinHierarchy:
    """One Site → Building → Level → Room chain that equipment can be attached to.

    `prefix` namespaces the URIs so concurrent runs (and the sample twin) do not collide.
    """

    def __init__(self, prefix: str, building_id: str | None = None, floor_id: str | None = None):
        # Derive the *ids* from the prefix too, not just the URIs. Two seeders sharing an id would
        # put two buildings in the twin claiming the same sbco:id, and — worse — two Levels sharing
        # an sbco:name, which the sbco:floor literal join (chain C, also used by ListDeviceDetails)
        # matches on: each building's device listing would then include the other's devices.
        slug = prefix.replace(":", "-")
        self.building_id = building_id or f"{slug}-bldg"
        # Likewise per building: a floor name shared across buildings makes that same join fan out
        # across all of them, which in a scale sweep multiplies every point's solutions by the
        # building count — inside the very query the harness is timing.
        self.floor_id = floor_id or f"{self.building_id}-F1"
        self.room_id = f"{self.floor_id}-room-1"
        self.site_uri = f"urn:{prefix}:site:{self.building_id}"
        self.building_uri = f"urn:{prefix}:building:{self.building_id}"
        self.floor_uri = f"urn:{prefix}:level:{self.building_id}:{self.floor_id}"
        self.room_uri = f"urn:{prefix}:room:{self.building_id}:{self.room_id}"

    def triples(self) -> list[str]:
        """The spatial nodes, as `INSERT DATA` body lines (indented, `.`-terminated)."""
        return [
            f'  <{self.site_uri}> a <{SBCO}Site> ; <{SBCO}id> "{_esc(self.building_id)}-site" ; '
            f'<{SBCO}name> "Perf Site" ; <{SBCO}hasPart> <{self.building_uri}> .',
            f'  <{self.building_uri}> a <{SBCO}Building> ; <{SBCO}id> "{_esc(self.building_id)}" ; '
            f'<{SBCO}name> "Perf Building" ; <{SBCO}hasPart> <{self.floor_uri}> .',
            f'  <{self.floor_uri}> a <{SBCO}Level> ; <{SBCO}id> "{_esc(self.floor_id)}" ; '
            f'<{SBCO}name> "{_esc(self.floor_id)}" ; <{SBCO}hasPart> <{self.room_uri}> .',
            f'  <{self.room_uri}> a <{SBCO}Room> ; <{SBCO}id> "{_esc(self.room_id)}" ; '
            f'<{SBCO}name> "Perf Room" .',
        ]

    def equipment_props(self) -> list[str]:
        """Properties that anchor an EquipmentExt into this hierarchy.

        `locatedIn` is what `OrphanPattern` traverses; `sbco:floor` is the denormalized literal the
        read paths project, and it doubles as chain C, so a device stays reachable even if the Room
        hop is later dropped from a fixture.
        """
        return [
            f"<{SBCO}locatedIn> <{self.room_uri}>",
            f'<{SBCO}floor> "{_esc(self.floor_id)}"',
        ]

    def uris(self) -> list[str]:
        """Every spatial node this hierarchy owns, for cleanup paths to delete."""
        return [self.site_uri, self.building_uri, self.floor_uri, self.room_uri]

    def point_props(self) -> list[str]:
        """Properties a PointExt needs for the ingress metadata cache to resolve its building.

        `sbco:building` is required by `IPointMetadataCache` (it is the telemetry `building` field
        and the Parquet lake partition key), and it is a literal, so it has to be repeated per point.
        """
        return [f'<{SBCO}building> "{_esc(self.building_id)}"']
