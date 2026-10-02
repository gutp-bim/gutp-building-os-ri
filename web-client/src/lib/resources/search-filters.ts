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
  /** Data-health conditions (Point only); see {@link SearchParams.freshness}. */
  freshness: HealthFreshness[];
  alarm: HealthAlarm[];
};

export type HealthFreshness = "fresh" | "stale" | "missing";
export type HealthAlarm = "warn" | "critical";

/** Selectable data-health values, in display order (the health API also knows `unknown`/`normal`). */
export const HEALTH_FRESHNESS_VALUES: readonly HealthFreshness[] = ["fresh", "stale", "missing"];
export const HEALTH_ALARM_VALUES: readonly HealthAlarm[] = ["warn", "critical"];

export const EMPTY_FILTERS: SearchFilters = {
  q: "",
  type: "",
  tags: [],
  deviceTypes: [],
  pointTypes: [],
  units: [],
  gatewayIds: [],
  freshness: [],
  alarm: [],
};

/** The facet groups that hold a list of selected values (everything but q / type / tags). */
export type FacetGroup =
  | "deviceTypes"
  | "pointTypes"
  | "units"
  | "gatewayIds"
  | "freshness"
  | "alarm";

/** The asset-attribute groups, served by `resources/search` + `resources/facets`. */
export const ATTRIBUTE_GROUPS = ["deviceTypes", "pointTypes", "units", "gatewayIds"] as const;

// URL parameter per field. `sel` (the selected tree node) is owned by the page and never touched here.
const PARAM = {
  q: "q",
  type: "type",
  tags: "tag",
  deviceTypes: "deviceType",
  pointTypes: "pointType",
  units: "unit",
  gatewayIds: "gatewayId",
  // Same names as the /health screen's URL, so a filtered view reads the same in both places.
  freshness: "freshness",
  alarm: "alarm",
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
    f.gatewayIds.length > 0 ||
    f.freshness.length > 0 ||
    f.alarm.length > 0
  );
}

/** True when a data-health condition is set — the search then goes to the health API. */
export function usesHealth(f: SearchFilters): boolean {
  return f.freshness.length > 0 || f.alarm.length > 0;
}

/** True when an asset-attribute facet is set — the search then goes to `resources/search`. */
export function usesAttributes(f: SearchFilters): boolean {
  return ATTRIBUTE_GROUPS.some((g) => f[g].length > 0);
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
    freshness: f.freshness.length > 0 ? f.freshness : undefined,
    alarm: f.alarm.length > 0 ? f.alarm : undefined,
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
    freshness: known(list(PARAM.freshness), HEALTH_FRESHNESS_VALUES),
    alarm: known(list(PARAM.alarm), HEALTH_ALARM_VALUES),
  };
}

// Health values are matched case-insensitively (a hand-written `?freshness=Stale` still works) and an
// unknown one is dropped rather than sent: the health API ignores it, which would widen the search.
function known<T extends string>(values: string[], allowed: readonly T[]): T[] {
  const out: T[] = [];
  for (const v of values) {
    const hit = allowed.find((a) => a === v.toLowerCase());
    if (hit && !out.includes(hit)) out.push(hit);
  }
  return out;
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
  for (const v of f.freshness) next.append(PARAM.freshness, v);
  for (const v of f.alarm) next.append(PARAM.alarm, v);
  return next;
}
