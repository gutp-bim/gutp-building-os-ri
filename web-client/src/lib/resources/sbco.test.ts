import { describe, expect, it } from "vitest";
import { sbcoClassName } from "./sbco";
import type { ResourceType } from "./types";

describe("sbcoClassName", () => {
  it("maps each resource type to its SBCO ontology class", () => {
    expect(sbcoClassName("building")).toBe("sbco:Building");
    expect(sbcoClassName("floor")).toBe("sbco:Level");
    expect(sbcoClassName("space")).toBe("sbco:Room");
    expect(sbcoClassName("device")).toBe("sbco:EquipmentExt");
    expect(sbcoClassName("point")).toBe("sbco:PointExt");
  });

  it("covers every ResourceType (no missing mapping)", () => {
    const all: ResourceType[] = ["building", "floor", "space", "device", "point"];
    for (const t of all) {
      expect(sbcoClassName(t)).toMatch(/^sbco:/);
    }
  });
});
