import { DEFAULT_HEALTH_QUERY, type HealthQuery } from "@/lib/health/query";
import {
  fetchPointHealth,
  fetchPointHealthSummary,
  type HealthPage,
  type HealthSummary,
} from "@/lib/health/repository";
import { fetchResourceFacets, searchResources } from "./repository";
import {
  HEALTH_ALARM_VALUES,
  HEALTH_FRESHNESS_VALUES,
  type HealthAlarm,
  type HealthFreshness,
} from "./search-filters";
import type {
  FacetValue,
  HealthFacets,
  ResourceFacetsResult,
  SearchHit,
  SearchParams,
} from "./types";

/** Page size of a health-routed search — the same as the resource search box's default. */
const HEALTH_SEARCH_LIMIT = 50;

type Deps = {
  searchResources: (params: SearchParams) => Promise<SearchHit[]>;
  fetchResourceFacets: (params: SearchParams) => Promise<ResourceFacetsResult>;
  fetchPointHealth: (query: HealthQuery) => Promise<HealthPage>;
  fetchPointHealthSummary: (query: HealthQuery) => Promise<HealthSummary>;
};

const DEFAULT_DEPS: Deps = { searchResources, fetchResourceFacets, fetchPointHealth, fetchPointHealthSummary };

/** True when the params carry a data-health condition (Point only), which only the health API can answer. */
function isHealthSearch(p: SearchParams): boolean {
  return (p.freshness?.length ?? 0) > 0 || (p.alarm?.length ?? 0) > 0;
}

// The health API's own scope filters. It matches q on pointId / name and ANDs the tags — the same
// meaning as the resource search — so the free-text and tag boxes keep working in health mode.
function healthQuery(p: SearchParams): HealthQuery {
  return {
    ...DEFAULT_HEALTH_QUERY,
    freshness: (p.freshness ?? []) as HealthQuery["freshness"],
    alarm: (p.alarm ?? []) as HealthQuery["alarm"],
    tags: p.tags ?? [],
    q: p.q,
    limit: HEALTH_SEARCH_LIMIT,
  };
}

/**
 * The search box's search. A data-health condition goes to the health API (one call — the asset
 * search and the health classifier are never joined in the UI, #454); everything else is the
 * resource search. A health row carries no digital-twin id, so the hit's `dtId` is **empty** (not a
 * stand-in): whoever selects it resolves the real node by pointId (`resolveRef`).
 */
export async function searchResourcesOrHealth(
  params: SearchParams,
  deps: Deps = DEFAULT_DEPS,
): Promise<SearchHit[]> {
  if (!isHealthSearch(params)) return deps.searchResources(params);
  const page = await deps.fetchPointHealth(healthQuery(params));
  return page.rows.map((r) => ({
    type: "point",
    dtId: "",
    id: r.pointId,
    name: r.name,
    buildingDtId: null,
  }));
}

function healthFacets(s: HealthSummary): HealthFacets {
  const fresh: Record<HealthFreshness, number> = { fresh: s.fresh, stale: s.stale, missing: s.missing };
  const alarm: Record<HealthAlarm, number> = { warn: s.alarmWarn, critical: s.alarmCritical };
  const list = <K extends string>(order: readonly K[], counts: Record<K, number>): FacetValue[] =>
    order.map((value) => ({ value, count: counts[value] }));
  return {
    freshness: list(HEALTH_FRESHNESS_VALUES, fresh),
    alarm: list(HEALTH_ALARM_VALUES, alarm),
    dataComplete: s.dataComplete,
  };
}

const NO_FACETS: ResourceFacetsResult = {
  total: 0,
  truncated: false,
  types: [],
  deviceTypes: [],
  pointTypes: [],
  units: [],
  gateways: [],
};

/**
 * Facet counts for the search box. Asset facets come from `resources/facets`; while Points are the
 * subject (type = point) the Freshness / Alarm counts come from the health summary, scoped by the
 * same free text and tags. In health mode the asset facets are not offered (the health API cannot
 * filter on them), so only the health counts are fetched. A failed health summary just leaves the
 * Health groups out — the asset facets and the search still work.
 */
export async function fetchFacetsWithHealth(
  params: SearchParams,
  deps: Deps = DEFAULT_DEPS,
): Promise<ResourceFacetsResult> {
  const summary = () =>
    deps.fetchPointHealthSummary({ ...DEFAULT_HEALTH_QUERY, tags: params.tags ?? [], q: params.q });

  if (isHealthSearch(params)) {
    const s = await summary();
    return {
      ...NO_FACETS,
      total: s.totalPoints,
      types: [{ value: "point", count: s.totalPoints }],
      health: healthFacets(s),
    };
  }

  if (params.type !== "point") return deps.fetchResourceFacets(params);

  const [facets, health] = await Promise.all([
    deps.fetchResourceFacets(params),
    summary().then(healthFacets, () => undefined),
  ]);
  return { ...facets, health };
}
