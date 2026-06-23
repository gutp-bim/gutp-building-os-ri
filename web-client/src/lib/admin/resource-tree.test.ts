import { describe, expect, it } from "vitest";
import { childTypeOf, hasChildren } from "./resource-tree";

describe("childTypeOf", () => {
  it("walks building → floor → space → device → point → leaf", () => {
    expect(childTypeOf("building")).toBe("floor");
    expect(childTypeOf("floor")).toBe("space");
    expect(childTypeOf("space")).toBe("device");
    expect(childTypeOf("device")).toBe("point");
    expect(childTypeOf("point")).toBeNull();
  });
});

describe("hasChildren", () => {
  it("is true for every non-leaf type and false for point", () => {
    expect(hasChildren("building")).toBe(true);
    expect(hasChildren("device")).toBe(true);
    expect(hasChildren("point")).toBe(false);
  });
});
