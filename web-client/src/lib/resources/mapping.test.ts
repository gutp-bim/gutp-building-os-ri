import type {
  Building,
  Point,
  ResourceSearchHit,
} from "@/lib/infra/aspida-client/generated/@types";
import { describe, expect, it } from "vitest";
import { toPointResource, toRef, toSearchHit } from "./mapping";

describe("toRef", () => {
  it("maps a dtId/id/name entity to a typed ResourceRef", () => {
    const b: Building = { dtId: "urn:b1", id: "B1", name: "Bldg 1" };
    expect(toRef("building", b)).toEqual({
      type: "building",
      dtId: "urn:b1",
      id: "B1",
      name: "Bldg 1",
    });
  });
});

describe("toPointResource", () => {
  it("normalizes undefined/null optional fields to null and renames type→kind", () => {
    const p: Point = { dtId: "urn:pt1", id: "PT001", name: "Temp" };
    expect(toPointResource(p)).toEqual({
      type: "point",
      dtId: "urn:pt1",
      id: "PT001",
      name: "Temp",
      writable: null,
      unit: null,
      scale: null,
      labels: null,
      specification: null,
      kind: null,
      expectedIntervalSeconds: null,
      alarmHigh: null,
      alarmLow: null,
      warnHigh: null,
      warnLow: null,
    });
  });

  it("carries through present fields", () => {
    const p: Point = {
      dtId: "urn:pt1",
      id: "PT001",
      name: "Temp",
      writable: true,
      unit: "°C",
      scale: 0.1,
      labels: "a,b",
      specification: "spec",
      type: "analog",
      interval: 60,
    };
    const r = toPointResource(p);
    expect(r.writable).toBe(true);
    expect(r.unit).toBe("°C");
    expect(r.scale).toBe(0.1);
    expect(r.kind).toBe("analog");
    expect(r.expectedIntervalSeconds).toBe(60);
  });

  it("reads opt-in alarm thresholds off the point (#158 Phase 2a)", () => {
    const p = {
      dtId: "urn:pt1",
      id: "PT001",
      name: "Temp",
      alarmHigh: 30,
      alarmLow: 5,
      warnHigh: 26,
      warnLow: 8,
    } as Point;
    const r = toPointResource(p);
    expect(r.alarmHigh).toBe(30);
    expect(r.alarmLow).toBe(5);
    expect(r.warnHigh).toBe(26);
    expect(r.warnLow).toBe(8);
  });
});

describe("toSearchHit", () => {
  it("maps a hit and defaults buildingDtId to null", () => {
    const h: ResourceSearchHit = {
      type: "device",
      dtId: "urn:d1",
      id: "D1",
      name: "AC",
    };
    expect(toSearchHit(h)).toEqual({
      type: "device",
      dtId: "urn:d1",
      id: "D1",
      name: "AC",
      buildingDtId: null,
    });
  });

  it("keeps a present buildingDtId", () => {
    const h: ResourceSearchHit = {
      type: "floor",
      dtId: "urn:f1",
      id: "F1",
      name: "1F",
      buildingDtId: "urn:b1",
    };
    expect(toSearchHit(h).buildingDtId).toBe("urn:b1");
  });
});
