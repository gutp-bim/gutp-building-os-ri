import { describe, expect, it } from "vitest";
import type { PointFreshness } from "@/lib/telemetry/freshness";
import { buildAttentionList } from "./aggregate";

const named = [
  { pointId: "p1", name: "室温" },
  { pointId: "p2", name: "湿度" },
  { pointId: "p3", name: "CO2" },
  { pointId: "p4", name: "電力" },
];

const freshness: PointFreshness[] = [
  { pointId: "p1", status: "fresh", ageSeconds: 10 },
  { pointId: "p2", status: "stale", ageSeconds: 900 },
  { pointId: "p3", status: "missing", ageSeconds: null },
  { pointId: "p4", status: "stale", ageSeconds: 5000 },
];

describe("buildAttentionList", () => {
  it("keeps only stale and missing points (drops fresh)", () => {
    const items = buildAttentionList(named, freshness);
    expect(items.map((i) => i.pointId).sort()).toEqual(["p2", "p3", "p4"]);
    expect(items.every((i) => i.status === "stale" || i.status === "missing")).toBe(true);
  });

  it("orders missing first, then stale by age descending (worst first)", () => {
    const items = buildAttentionList(named, freshness);
    expect(items.map((i) => i.pointId)).toEqual(["p3", "p4", "p2"]);
  });

  it("joins the point name, falling back to the id when unknown", () => {
    const items = buildAttentionList([], freshness);
    expect(items.find((i) => i.pointId === "p2")?.name).toBe("p2");
    const named2 = buildAttentionList(named, freshness);
    expect(named2.find((i) => i.pointId === "p2")?.name).toBe("湿度");
  });

  it("returns an empty list when everything is fresh", () => {
    const allFresh: PointFreshness[] = named.map((n) => ({
      pointId: n.pointId,
      status: "fresh",
      ageSeconds: 1,
    }));
    expect(buildAttentionList(named, allFresh)).toEqual([]);
  });
});
