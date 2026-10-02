import type { HealthPage, HealthSummary } from "@/lib/health/repository";
import { describe, expect, it, vi } from "vitest";
import { fetchFacetsWithHealth, searchResourcesOrHealth } from "./search-dispatch";
import type { ResourceFacetsResult, SearchHit } from "./types";

const assetFacets: ResourceFacetsResult = {
  total: 5, truncated: false,
  types: [{ value: "point", count: 5 }],
  deviceTypes: [{ value: "AHU", count: 5 }], pointTypes: [], units: [], gateways: [],
};
const summary: HealthSummary = {
  totalPoints: 10, fresh: 7, stale: 2, missing: 1, unknown: 0, alarmWarn: 3, alarmCritical: 1,
  dataComplete: true, indexState: "ready",
};
const hit: SearchHit = { type: "point", dtId: "urn:p", id: "P1", name: "p", buildingDtId: null };

function deps(over: Record<string, unknown> = {}) {
  return {
    searchResources: vi.fn().mockResolvedValue([hit]),
    fetchResourceFacets: vi.fn().mockResolvedValue(assetFacets),
    fetchPointHealth: vi.fn().mockResolvedValue({
      rows: [{ pointId: "P9", name: "Stale one" }], total: 1, limit: 50, offset: 0, dataComplete: true, indexState: "ready",
    } as unknown as HealthPage),
    fetchPointHealthSummary: vi.fn().mockResolvedValue(summary),
    ...over,
  };
}

describe("searchResourcesOrHealth (#454)", () => {
  it("sends a plain search to the resource search", async () => {
    const d = deps();
    expect(await searchResourcesOrHealth({ q: "x", deviceTypes: ["AHU"] }, d)).toEqual([hit]);
    expect(d.fetchPointHealth).not.toHaveBeenCalled();
  });

  it("sends a data-health condition to the health API alone, carrying q and tags", async () => {
    const d = deps();
    const hits = await searchResourcesOrHealth({ q: "sat", tags: ["hvac"], freshness: ["stale"], alarm: ["critical"] }, d);

    expect(d.searchResources).not.toHaveBeenCalled();
    expect(d.fetchPointHealth).toHaveBeenCalledWith(
      expect.objectContaining({ q: "sat", tags: ["hvac"], freshness: ["stale"], alarm: ["critical"], limit: 50 }),
    );
    expect(hits).toEqual([{ type: "point", dtId: "", id: "P9", name: "Stale one", buildingDtId: null }]);
  });
});

describe("fetchFacetsWithHealth (#454)", () => {
  it("adds Freshness / Alarm counts while Points are the subject", async () => {
    const d = deps();
    const f = await fetchFacetsWithHealth({ type: "point", q: "x", tags: ["t"] }, d);

    expect(f.deviceTypes).toEqual(assetFacets.deviceTypes);
    expect(f.health?.freshness).toEqual([
      { value: "fresh", count: 7 }, { value: "stale", count: 2 }, { value: "missing", count: 1 },
    ]);
    expect(f.health?.alarm).toEqual([{ value: "warn", count: 3 }, { value: "critical", count: 1 }]);
    expect(f.health?.dataComplete).toBe(true);
    expect(d.fetchPointHealthSummary).toHaveBeenCalledWith(expect.objectContaining({ q: "x", tags: ["t"] }));
  });

  it("does not ask for health counts for another type", async () => {
    const d = deps();
    const f = await fetchFacetsWithHealth({ type: "device" }, d);
    expect(f.health).toBeUndefined();
    expect(d.fetchPointHealthSummary).not.toHaveBeenCalled();
  });

  it("in health mode fetches only the health counts, not the asset facets", async () => {
    const d = deps();
    const f = await fetchFacetsWithHealth({ type: "point", freshness: ["stale"] }, d);

    expect(d.fetchResourceFacets).not.toHaveBeenCalled();
    // the Point count is the filtered total (the list's `total`), not the unfiltered summary's 10
    expect(f.types).toEqual([{ value: "point", count: 1 }]);
    expect(f.total).toBe(1);
    expect(f.deviceTypes).toEqual([]);
    expect(f.health?.freshness).toHaveLength(3);
  });

  it("a failed health summary leaves the asset facets intact", async () => {
    const d = deps({ fetchPointHealthSummary: vi.fn().mockRejectedValue(new Error("boom")) });
    const f = await fetchFacetsWithHealth({ type: "point" }, d);
    expect(f.deviceTypes).toEqual(assetFacets.deviceTypes);
    expect(f.health).toBeUndefined();
  });

  it("carries the provisional flag while the last-seen index is warming", async () => {
    const d = deps({ fetchPointHealthSummary: vi.fn().mockResolvedValue({ ...summary, dataComplete: false, indexState: "warming" }) });
    const f = await fetchFacetsWithHealth({ type: "point" }, d);
    expect(f.health?.dataComplete).toBe(false);
  });
});
