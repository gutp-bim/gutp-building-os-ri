import type { ResourceType } from "./types";

/**
 * Maps the explorer's simplified {@link ResourceType} to its SBCO ontology class (the actual RDF
 * class the digital twin uses). Mirrors the backend OxiGraphOntology mapping
 * (DotNet/BuildingOS.Shared/Infrastructure/OxiGraph/OxiGraphOntology.cs):
 *   building → sbco:Building, floor → sbco:Level, space → sbco:Room,
 *   device → sbco:EquipmentExt, point → sbco:PointExt.
 * See docs/architecture/standard-mapping.md for the SBCO ↔ Brick/REC/IFC/DTDL mapping.
 */
const SBCO_CLASS: Record<ResourceType, string> = {
  building: "sbco:Building",
  floor: "sbco:Level",
  space: "sbco:Room",
  device: "sbco:EquipmentExt",
  point: "sbco:PointExt",
};

/** The SBCO ontology class name (prefixed form) for a resource type, e.g. "sbco:Building". */
export function sbcoClassName(type: ResourceType): string {
  return SBCO_CLASS[type];
}
