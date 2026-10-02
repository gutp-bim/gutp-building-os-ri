import { normalizeTags } from "./search";
import type { ResourceType, SearchParams } from "./types";

/**
 * The complete state of the resource search form (#454): free text, type, tags and the structured-attribute
 * facets. It is the one thing mirrored to the URL, so a filtered view can be shared and reloaded.
 */
export type SearchFilters = {
  q: string;
  type: "" | ResourceType;
  tags: string[];
  deviceTypes: string[];
  pointTypes: string[];
  units: string[];
  gatewayIds: string[];
};

export const EMPTY_FILTERS: SearchFilters = {
  q: "",
  type: "",
  tags: [],
  deviceTypes: [],
  pointTypes: [],
  units: [],
  gatewayIds: [],
};

/** The facet groups that hold a list of selected values (everything but q / type / tags). */
export type FacetGroup = "deviceTypes" | "pointTypes" | "units" | "gatewayIds";

// URL parameter per field. `sel` (the selected tree node) is owned by the page and never touched here.
const PARAM = {
  q: "q",
  type: "type",
  tags: "tag",
  deviceTypes: "deviceType",
  pointTypes: "pointType",
  units: "unit",
  gatewayIds: "gatewayId",
} as const;

const TYPES: readonly ResourceType[] = ["building", "floor", "space", "device", "point"];

/** True when anything narrows the search — an empty form searches nothing. */
export function hasCriteria(f: SearchFilters): boolean {
  return (
    f.q.trim() !== "" ||
    f.type !== "" ||
    f.tags.length > 0 ||
    f.deviceTypes.length > 0 ||
    f.pointTypes.length > 0 ||
    f.units.length > 0 ||
    f.gatewayIds.length > 0
  );
}

/** Adds the value when absent, removes it when present. Order of the remaining values is kept. */
export function toggleValue(list: readonly string[], value: string): string[] {
  return list.includes(value) ? list.filter((v) => v !== value) : [...list, value];
}

/** The params the search / facets repository functions take. */
export function toSearchParams(f: SearchFilters): SearchParams {
  const q = f.q.trim();
  return {
    q: q || undefined,
    type: f.type || undefined,
    tags: f.tags.length > 0 ? f.tags : undefined,
    deviceTypes: f.deviceTypes.length > 0 ? f.deviceTypes : undefined,
    pointTypes: f.pointTypes.length > 0 ? f.pointTypes : undefined,
    units: f.units.length > 0 ? f.units : undefined,
    gatewayIds: f.gatewayIds.length > 0 ? f.gatewayIds : undefined,
  };
}

/** Reads the filters from a URL. Unknown `type` values and blanks are dropped. */
export function filtersFromParams(params: URLSearchParams): SearchFilters {
  const list = (key: string) => normalizeTags(params.getAll(key));
  const type = params.get(PARAM.type) ?? "";
  return {
    q: params.get(PARAM.q) ?? "",
    type: (TYPES as readonly string[]).includes(type) ? (type as ResourceType) : "",
    tags: list(PARAM.tags),
    deviceTypes: list(PARAM.deviceTypes),
    pointTypes: list(PARAM.pointTypes),
    units: list(PARAM.units),
    gatewayIds: list(PARAM.gatewayIds),
  };
}

/**
 * Writes the filters onto `base` (other params such as `sel` are kept) and returns the new params.
 * Empty fields are removed, so a cleared form leaves a clean URL.
 */
export function filtersToParams(f: SearchFilters, base: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(base.toString());
  for (const key of Object.values(PARAM)) next.delete(key);
  if (f.q.trim()) next.set(PARAM.q, f.q.trim());
  if (f.type) next.set(PARAM.type, f.type);
  for (const t of f.tags) next.append(PARAM.tags, t);
  for (const v of f.deviceTypes) next.append(PARAM.deviceTypes, v);
  for (const v of f.pointTypes) next.append(PARAM.pointTypes, v);
  for (const v of f.units) next.append(PARAM.units, v);
  for (const v of f.gatewayIds) next.append(PARAM.gatewayIds, v);
  return next;
}
