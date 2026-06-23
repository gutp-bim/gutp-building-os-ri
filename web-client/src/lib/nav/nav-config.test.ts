import { describe, expect, it } from "vitest";
import { type NavItem, visibleNavItems } from "./nav-config";

describe("visibleNavItems", () => {
  it("returns only items for the requested workspace", () => {
    const operatorItems = visibleNavItems("operator", []);
    expect(operatorItems.length).toBeGreaterThan(0);
    expect(operatorItems.every((i) => i.workspace === "operator")).toBe(true);

    const adminItems = visibleNavItems("admin", []);
    expect(adminItems.every((i) => i.workspace === "admin")).toBe(true);
    expect(adminItems.map((i) => i.href)).toContain("/admin/users");
  });

  it("hides permission-gated items when the permission is missing", () => {
    const items: NavItem[] = [
      { label: "Open", href: "/open", workspace: "platform" },
      {
        label: "Gated",
        href: "/gated",
        workspace: "platform",
        permission: { resourceType: "point", action: "control" },
      },
    ];
    const visible = visibleNavItems("platform", [], items).map((i) => i.label);
    expect(visible).toEqual(["Open"]);
  });

  it("shows permission-gated items when the permission is held", () => {
    const items: NavItem[] = [
      {
        label: "Gated",
        href: "/gated",
        workspace: "platform",
        permission: { resourceType: "point", action: "control" },
      },
    ];
    const visible = visibleNavItems("platform", ["point:*:control"], items).map((i) => i.label);
    expect(visible).toEqual(["Gated"]);
  });
});
