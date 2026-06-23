import { describe, expect, it } from "vitest";
import { buildPermissionString, validatePermissionInput } from "./permission-edit";

describe("buildPermissionString", () => {
  it("builds the abbreviated picker format", () => {
    expect(
      buildPermissionString({ resourceType: "device", resourceId: "dev-1", actions: ["read", "write"] }),
    ).toBe("d:dev-1:rw");
  });

  it("emits actions in canonical r/w/a order regardless of selection order", () => {
    expect(
      buildPermissionString({ resourceType: "building", resourceId: "b1", actions: ["admin", "read"] }),
    ).toBe("b:b1:ra");
  });

  it("trims the resource id", () => {
    expect(
      buildPermissionString({ resourceType: "group", resourceId: "  grp-1  ", actions: ["read"] }),
    ).toBe("g:grp-1:r");
  });
});

describe("validatePermissionInput", () => {
  it("requires a resource id", () => {
    expect(validatePermissionInput({ resourceId: "  ", actions: ["read"] }).ok).toBe(false);
  });

  it("requires at least one action", () => {
    expect(validatePermissionInput({ resourceId: "x", actions: [] }).ok).toBe(false);
  });

  it("accepts a valid input", () => {
    expect(validatePermissionInput({ resourceId: "x", actions: ["read"] })).toEqual({ ok: true });
  });
});
