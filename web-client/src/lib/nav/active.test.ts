import { describe, expect, it } from "vitest";
import { isNavItemActive, workspaceForPath } from "./active";
import type { NavItem } from "./nav-config";

describe("isNavItemActive", () => {
  const item: NavItem = { label: "建物", href: "/buildings", workspace: "operator" };

  it("matches the exact page and its child routes", () => {
    expect(isNavItemActive("/buildings", item)).toBe(true);
    expect(isNavItemActive("/buildings/abc", item)).toBe(true);
  });

  it("does not match a sibling whose path merely shares a prefix", () => {
    expect(isNavItemActive("/buildings-archive", item)).toBe(false);
    expect(isNavItemActive("/floors", item)).toBe(false);
  });
});

describe("workspaceForPath", () => {
  it("resolves operator paths to the operator workspace", () => {
    expect(workspaceForPath("/buildings")).toBe("operator");
    expect(workspaceForPath("/points/p-1")).toBe("operator");
  });

  it("resolves admin and platform paths to their workspaces", () => {
    expect(workspaceForPath("/admin/users")).toBe("admin");
    expect(workspaceForPath("/platform/status")).toBe("platform");
  });

  it("returns null for paths no nav item owns", () => {
    expect(workspaceForPath("/")).toBeNull();
  });
});
