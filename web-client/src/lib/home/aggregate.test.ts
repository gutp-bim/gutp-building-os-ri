import { describe, expect, it } from "vitest";
import type { PointAlarm } from "@/lib/telemetry/alarm";
import type { PointFreshness } from "@/lib/telemetry/freshness";
import { activeAlarms, buildAttentionList } from "./aggregate";

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

  it("merges value alarms and sorts critical → warn → missing → stale (#158 Phase 2a)", () => {
    const alarms: PointAlarm[] = [
      { pointId: "p1", status: "warn", value: 27, breach: "high" },
      { pointId: "p2", status: "critical", value: 40, breach: "high" }, // p2 is also stale
    ];
    const items = buildAttentionList(named, freshness, alarms);
    // p2 is both critical (alarm) and stale (freshness) → kept as the worse (critical).
    expect(items.map((i) => i.pointId)).toEqual(["p2", "p1", "p3", "p4"]);
    const p2 = items.find((i) => i.pointId === "p2");
    expect(p2?.status).toBe("critical");
    expect(p2?.value).toBe(40);
    expect(p2?.breach).toBe("high");
    // p1 was fresh, so it only surfaces because of its warn alarm.
    expect(items.find((i) => i.pointId === "p1")?.status).toBe("warn");
  });

  it("activeAlarms drops alarms whose data is stale/missing (untrustworthy value)", () => {
    const alarms: PointAlarm[] = [
      { pointId: "p1", status: "critical", value: 40, breach: "high" }, // fresh → kept
      { pointId: "p2", status: "critical", value: 40, breach: "high" }, // stale → dropped
      { pointId: "p3", status: "warn", value: 27, breach: "high" }, // missing → dropped
    ];
    const kept = activeAlarms(alarms, freshness);
    expect(kept.map((a) => a.pointId)).toEqual(["p1"]);
  });

  it("ignores ok/unknown alarms (only critical/warn surface)", () => {
    const alarms: PointAlarm[] = [
      { pointId: "p1", status: "ok", value: 20, breach: null },
      { pointId: "p2", status: "unknown", value: null, breach: null },
    ];
    // p1 fresh + ok → not listed; p2 stale (freshness) still lists as stale.
    const items = buildAttentionList(named, freshness, alarms);
    expect(items.find((i) => i.pointId === "p1")).toBeUndefined();
    expect(items.find((i) => i.pointId === "p2")?.status).toBe("stale");
  });

  it("carries the device and space names from the named points", () => {
    const namedWithCtx = [
      { pointId: "p2", name: "湿度", deviceName: "AHU-1", spaceName: "会議室A" },
      { pointId: "p3", name: "CO2", deviceName: "CO2-Sensor-01", spaceName: "会議室A" },
    ];
    const items = buildAttentionList(namedWithCtx, freshness);
    const p3 = items.find((i) => i.pointId === "p3");
    expect(p3?.spaceName).toBe("会議室A");
    expect(p3?.deviceName).toBe("CO2-Sensor-01");
    // Points without context still resolve (undefined device/space, name falls back to id).
    const p4 = items.find((i) => i.pointId === "p4");
    expect(p4?.deviceName).toBeUndefined();
  });
});
