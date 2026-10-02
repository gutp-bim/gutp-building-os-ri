import { describe, expect, it } from "vitest";
import {
  EMPTY_FILTERS,
  usesAttributes,
  usesHealth,
  filtersFromParams,
  filtersToParams,
  hasCriteria,
  toggleValue,
  toSearchParams,
} from "./search-filters";

describe("hasCriteria", () => {
  it("is false for the empty form and for whitespace-only text", () => {
    expect(hasCriteria(EMPTY_FILTERS)).toBe(false);
    expect(hasCriteria({ ...EMPTY_FILTERS, q: "   " })).toBe(false);
  });

  it.each([
    ["q", { q: "vav" }],
    ["type", { type: "point" as const }],
    ["tags", { tags: ["hvac"] }],
    ["deviceTypes", { deviceTypes: ["AHU"] }],
    ["pointTypes", { pointTypes: ["CO2"] }],
    ["units", { units: ["ppm"] }],
    ["gatewayIds", { gatewayIds: ["GW-1"] }],
    ["freshness", { freshness: ["stale" as const] }],
    ["alarm", { alarm: ["critical" as const] }],
  ])("is true when %s is set", (_name, patch) => {
    expect(hasCriteria({ ...EMPTY_FILTERS, ...patch })).toBe(true);
  });
});

describe("toggleValue", () => {
  it("adds an absent value and removes a present one, keeping order", () => {
    expect(toggleValue(["a", "b"], "c")).toEqual(["a", "b", "c"]);
    expect(toggleValue(["a", "b", "c"], "b")).toEqual(["a", "c"]);
  });
});

describe("toSearchParams", () => {
  it("omits empty fields and trims q", () => {
    expect(toSearchParams({ ...EMPTY_FILTERS, q: " x ", units: ["ppm"] })).toEqual({
      q: "x",
      type: undefined,
      tags: undefined,
      deviceTypes: undefined,
      pointTypes: undefined,
      units: ["ppm"],
      gatewayIds: undefined,
      freshness: undefined,
      alarm: undefined,
    });
  });
});

describe("URL round trip (#454)", () => {
  const full = {
    q: "vav",
    type: "point" as const,
    tags: ["hvac", "critical"],
    deviceTypes: ["AHU", "VAV"],
    pointTypes: ["Temperature"],
    units: ["degC"],
    gatewayIds: ["GW-1"],
    freshness: ["stale" as const, "missing" as const],
    alarm: ["critical" as const],
  };

  it("survives writing to and reading from the URL", () => {
    const params = filtersToParams(full, new URLSearchParams());
    expect(filtersFromParams(new URLSearchParams(params.toString()))).toEqual(full);
  });

  it("uses repeated params for lists, matching the API query", () => {
    const qs = filtersToParams(full, new URLSearchParams()).toString();
    expect(qs).toContain("deviceType=AHU&deviceType=VAV");
    expect(qs).toContain("tag=hvac&tag=critical");
  });

  it("keeps unrelated params such as sel, and replaces previous filter params", () => {
    const base = new URLSearchParams("sel=point%3AP1&q=old&unit=old");
    const out = filtersToParams({ ...EMPTY_FILTERS, q: "new" }, base);
    expect(out.get("sel")).toBe("point:P1");
    expect(out.getAll("q")).toEqual(["new"]);
    expect(out.has("unit")).toBe(false);
  });

  it("leaves a clean URL for a cleared form", () => {
    expect(filtersToParams(EMPTY_FILTERS, new URLSearchParams("q=x&tag=a")).toString()).toBe("");
  });

  it("drops an unknown type and blank list entries when reading", () => {
    const f = filtersFromParams(new URLSearchParams("type=bogus&tag=&tag=%20&unit=ppm&unit=ppm"));
    expect(f.type).toBe("");
    expect(f.tags).toEqual([]);
    expect(f.units).toEqual(["ppm"]);
  });
});

describe("health vs attribute routing (#454)", () => {
  it("tells which API a form needs", () => {
    expect(usesHealth({ ...EMPTY_FILTERS, freshness: ["stale"] })).toBe(true);
    expect(usesHealth({ ...EMPTY_FILTERS, alarm: ["warn"] })).toBe(true);
    expect(usesHealth({ ...EMPTY_FILTERS, units: ["ppm"] })).toBe(false);
    expect(usesAttributes({ ...EMPTY_FILTERS, units: ["ppm"] })).toBe(true);
    expect(usesAttributes({ ...EMPTY_FILTERS, freshness: ["stale"] })).toBe(false);
  });

  it("reads health values case-insensitively and drops unknown ones", () => {
    const f = filtersFromParams(new URLSearchParams("freshness=Stale&freshness=bogus&freshness=stale&alarm=CRITICAL&alarm=normal"));
    expect(f.freshness).toEqual(["stale"]);
    expect(f.alarm).toEqual(["critical"]);
  });
});
