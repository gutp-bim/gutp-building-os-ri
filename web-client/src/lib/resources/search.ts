import type { ResourceType, SearchParams } from "./types";

const DEFAULT_LIMIT = 50;
const MAX_LIMIT = 200;

/** The shape sent to the aspida `resources/search` endpoint query. */
export type NormalizedSearchQuery = {
  q?: string;
  type?: ResourceType;
  buildingId?: string;
  tag?: string[];
  deviceType?: string[];
  pointType?: string[];
  unit?: string[];
  gatewayId?: string[];
  limit: number;
  offset: number;
};

type AttrQuery = Pick<NormalizedSearchQuery, "deviceType" | "pointType" | "unit" | "gatewayId">;

/** Trim/dedupe each facet list and omit the empty ones, so the wire query only carries real constraints. */
function normalizeAttributes(input: SearchParams): AttrQuery {
  const pick = (v: readonly string[] | undefined) => {
    const out = normalizeTags(v);
    return out.length > 0 ? out : undefined;
  };
  return {
    deviceType: pick(input.deviceTypes),
    pointType: pick(input.pointTypes),
    unit: pick(input.units),
    gatewayId: pick(input.gatewayIds),
  };
}

/** The query of `resources/facets`: the search filters without paging. */
export function normalizeFacetQuery(
  input: SearchParams,
): Omit<NormalizedSearchQuery, "limit" | "offset"> {
  const { limit: _limit, offset: _offset, ...rest } = normalizeSearchParams(input);
  return rest;
}

/**
 * Pure normalization of user-supplied search params: trim/blank-drop strings, clamp limit to
 * 1..200 (default 50), floor offset at 0. Tags are trimmed, blank-dropped and de-duplicated
 * (order preserved); empty/absent → omit `tag`. Keeps the wire query small and predictable.
 */
export function normalizeSearchParams(
  input: SearchParams,
): NormalizedSearchQuery {
  const q = input.q?.trim();
  const buildingId = input.buildingId?.trim();
  const limit = Math.min(MAX_LIMIT, Math.max(1, input.limit ?? DEFAULT_LIMIT));
  const offset = Math.max(0, input.offset ?? 0);

  const tag = normalizeTags(input.tags);

  return {
    q: q ? q : undefined,
    type: input.type ?? undefined,
    buildingId: buildingId ? buildingId : undefined,
    tag: tag.length > 0 ? tag : undefined,
    ...normalizeAttributes(input),
    limit,
    offset,
  };
}

/** Trim, drop blanks, de-duplicate (first occurrence wins), preserving order. */
export function normalizeTags(tags: readonly string[] | undefined): string[] {
  if (!tags) return [];
  const seen = new Set<string>();
  const out: string[] = [];
  for (const raw of tags) {
    const t = raw.trim();
    if (t && !seen.has(t)) {
      seen.add(t);
      out.push(t);
    }
  }
  return out;
}
