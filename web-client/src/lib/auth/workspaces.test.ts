import { describe, expect, it } from "vitest";
import {
  canAccessWorkspace,
  defaultWorkspace,
  workspacesForRole,
} from "./workspaces";

describe("workspacesForRole", () => {
  it("gives admins all three workspaces in order", () => {
    expect(workspacesForRole("admin")).toEqual(["operator", "admin", "platform"]);
  });

  it("limits operator and viewer to the operator workspace", () => {
    expect(workspacesForRole("operator")).toEqual(["operator"]);
    expect(workspacesForRole("viewer")).toEqual(["operator"]);
  });

  it("returns no workspaces for an unknown role", () => {
    expect(workspacesForRole(null)).toEqual([]);
  });
});

describe("defaultWorkspace", () => {
  it("returns the first workspace for the role", () => {
    expect(defaultWorkspace("admin")).toBe("operator");
    expect(defaultWorkspace("viewer")).toBe("operator");
  });

  it("returns null when the role has no workspaces", () => {
    expect(defaultWorkspace(null)).toBeNull();
  });
});

describe("canAccessWorkspace", () => {
  it("allows admins into the platform workspace but not viewers", () => {
    expect(canAccessWorkspace("admin", "platform")).toBe(true);
    expect(canAccessWorkspace("viewer", "platform")).toBe(false);
    expect(canAccessWorkspace("operator", "admin")).toBe(false);
  });
});
