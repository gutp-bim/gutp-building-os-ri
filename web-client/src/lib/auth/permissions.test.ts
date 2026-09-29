import { describe, expect, it } from "vitest";
import { hasPermission } from "./permissions";

describe("hasPermission", () => {
  it("does not treat '*' as a wildcard type (#505 — the API never did)", () => {
    expect(hasPermission(["*:*:*"], "point", "control")).toBe(false);
    expect(hasPermission(["*:*:*"], "building", "read")).toBe(false);
  });

  it("matches resource type and action exactly", () => {
    const perms = ["point:p-1:read,write,control"];
    expect(hasPermission(perms, "point", "control")).toBe(true);
    expect(hasPermission(perms, "point", "read")).toBe(true);
    expect(hasPermission(perms, "point", "delete")).toBe(false);
    expect(hasPermission(perms, "device", "read")).toBe(false);
  });

  it("does not treat '*' as a wildcard action", () => {
    expect(hasPermission(["device:d-1:*"], "device", "control")).toBe(false);
  });

  it("returns false for empty permissions or malformed entries", () => {
    expect(hasPermission([], "point", "read")).toBe(false);
    expect(hasPermission(["garbage"], "point", "read")).toBe(false);
  });
});
