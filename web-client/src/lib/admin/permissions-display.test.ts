import { describe, expect, it } from "vitest";
import {
  actionLabel,
  parsePermission,
  resourceTypeColor,
} from "./permissions-display";

describe("parsePermission", () => {
  it("expands the abbreviated resource type and splits comma actions", () => {
    expect(parsePermission("d:abc123:r,w")).toEqual({
      resourceType: "device",
      resourceId: "abc123",
      actions: ["r", "w"],
    });
  });

  it("splits the backend's concatenated abbreviation form (rw / rwa)", () => {
    expect(parsePermission("d:abc:rw")?.actions).toEqual(["r", "w"]);
    expect(parsePermission("d:abc:rwa")?.actions).toEqual(["r", "w", "a"]);
  });

  it("keeps a single full action name as one token", () => {
    expect(parsePermission("d:abc:read")?.actions).toEqual(["read"]);
  });

  it("handles a single action and group abbreviation", () => {
    expect(parsePermission("g:grp-1:r")).toEqual({
      resourceType: "group",
      resourceId: "grp-1",
      actions: ["r"],
    });
  });

  it("keeps an unknown type abbreviation as-is", () => {
    expect(parsePermission("x:id:r")?.resourceType).toBe("x");
  });

  it("returns null for malformed strings", () => {
    expect(parsePermission("d:abc")).toBeNull(); // too few parts
    expect(parsePermission("d:abc:r:extra")).toBeNull(); // too many parts
    expect(parsePermission("d::r")).toBeNull(); // empty id
  });

  it("tolerates an empty actions segment", () => {
    expect(parsePermission("b:bldg:")).toEqual({
      resourceType: "building",
      resourceId: "bldg",
      actions: [],
    });
  });
});

describe("resourceTypeColor", () => {
  it("returns a distinct class per known type, gray for unknown", () => {
    expect(resourceTypeColor("building")).toContain("purple");
    expect(resourceTypeColor("group")).toContain("pink");
    expect(resourceTypeColor("nope")).toContain("gray");
  });
});

describe("actionLabel", () => {
  it("maps r/w/a (and full names) to Japanese, falls back to the raw code", () => {
    expect(actionLabel("r")).toBe("読み取り");
    expect(actionLabel("w")).toBe("書き込み");
    expect(actionLabel("a")).toBe("管理");
    expect(actionLabel("read")).toBe("読み取り");
    expect(actionLabel("admin")).toBe("管理");
    expect(actionLabel("x")).toBe("x");
  });
});
