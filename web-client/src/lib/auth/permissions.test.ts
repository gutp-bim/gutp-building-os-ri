import { describe, expect, it } from "vitest";
import { hasPermission } from "./permissions";

describe("hasPermission", () => {
  it("grants everything for the admin wildcard", () => {
    expect(hasPermission(["*:*:*"], "point", "control")).toBe(true);
    expect(hasPermission(["*:*:*"], "building", "read")).toBe(true);
  });

  it("matches resource type and action exactly", () => {
    const perms = ["point:*:read,write,control"];
    expect(hasPermission(perms, "point", "control")).toBe(true);
    expect(hasPermission(perms, "point", "read")).toBe(true);
    expect(hasPermission(perms, "point", "delete")).toBe(false);
    expect(hasPermission(perms, "device", "read")).toBe(false);
  });

  it("treats an action wildcard as all actions", () => {
    expect(hasPermission(["device:*:*"], "device", "control")).toBe(true);
  });

  it("returns false for empty permissions or malformed entries", () => {
    expect(hasPermission([], "point", "read")).toBe(false);
    expect(hasPermission(["garbage"], "point", "read")).toBe(false);
  });
});
