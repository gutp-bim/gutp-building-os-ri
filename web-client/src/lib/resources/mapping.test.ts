import type {
  Building,
  Point,
  ResourceSearchHit,
} from "@/lib/infra/aspida-client/generated/@types";
import { describe, expect, it } from "vitest";
import { toPointResource, toRef, toSearchHit,
  toPointDetail,
} from "./mapping";

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
      specification: null,
      kind: null,
      expectedIntervalSeconds: null,
      alarmHigh: null,
      alarmLow: null,
      warnHigh: null,
      warnLow: null,
      objectTypeBacnet: null,
      instanceNoBacnet: null,
      deviceIdBacnet: null,
      minPresValue: null,
      maxPresValue: null,
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

describe("toPointResource — BACnet addressing and control range (#350 4a)", () => {
  it("carries the BACnet addressing fields the point detail renders", () => {
    const r = toPointResource({
      dtId: "urn:pt:1",
      id: "PT001",
      name: "室温",
      objectTypeBacnet: "analogInput",
      instanceNoBacnet: 0,
      deviceIdBacnet: "D1",
    });
    expect(r.objectTypeBacnet).toBe("analogInput");
    // 0 is a valid instance number — it must survive, not collapse to null.
    expect(r.instanceNoBacnet).toBe(0);
    expect(r.deviceIdBacnet).toBe("D1");
  });

  it("carries the BACnet raw span (display-only fallback, ADR-0005)", () => {
    const r = toPointResource({
      dtId: "urn:pt:1",
      id: "PT001",
      name: "室温",
      minPresValue: -10,
      maxPresValue: 50,
    });
    expect(r.minPresValue).toBe(-10);
    expect(r.maxPresValue).toBe(50);
  });

  it("normalizes absent addressing to null rather than undefined", () => {
    const r = toPointResource({ dtId: "urn:pt:1", id: "PT001", name: "室温" });
    expect(r.objectTypeBacnet).toBeNull();
    expect(r.instanceNoBacnet).toBeNull();
    expect(r.deviceIdBacnet).toBeNull();
    expect(r.minPresValue).toBeNull();
    expect(r.maxPresValue).toBeNull();
  });
});

describe("toPointDetail (#350 4a)", () => {
  it("maps the point and normalizes absent spatial context to null", () => {
    const d = toPointDetail({ point: { dtId: "urn:pt:1", id: "PT001", name: "室温" } });
    expect(d.point.id).toBe("PT001");
    expect(d.device).toBeNull();
    expect(d.floor).toBeNull();
    expect(d.space).toBeNull();
    expect(d.controlSchema).toBeNull();
  });

  it("maps the device attributes the detail pane renders", () => {
    const d = toPointDetail({
      point: { dtId: "urn:pt:1", id: "PT001", name: "室温" },
      device: {
        dtId: "urn:dev:1",
        id: "DEV1",
        name: "AHU-1",
        deviceType: "ahu",
        supplier: "ACME",
        gatewayId: "gw1",
      },
    });
    expect(d.device).toMatchObject({
      type: "device",
      id: "DEV1",
      deviceType: "ahu",
      supplier: "ACME",
      gatewayId: "gw1",
      owner: null,
    });
  });

  it("maps the control schema, which is the authority for the write range (ADR-0005)", () => {
    const d = toPointDetail({
      point: { dtId: "urn:pt:1", id: "PT001", name: "室温" },
      controlSchema: { dataType: "analog", minValue: 16, maxValue: 30 },
    });
    expect(d.controlSchema).toEqual({
      dataType: "analog",
      enumLabels: null,
      minValue: 16,
      maxValue: 30,
    });
  });
});

