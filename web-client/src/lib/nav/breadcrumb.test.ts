import { describe, expect, it } from "vitest";
import { breadcrumbForPath } from "./breadcrumb";

describe("breadcrumbForPath", () => {
  it("returns workspace + page for a child page, with the last crumb unlinked", () => {
    const crumbs = breadcrumbForPath("/admin/users");
    expect(crumbs).toEqual([
      { label: "管理", href: "/admin/users" },
      { label: "ユーザー" }, // current page, no href
    ]);
  });

  it("always shows workspace + page, even on a detail deep-link page", () => {
    // /buildings is a hidden detail-route base (page "建物"); the workspace crumb links to the
    // operator landing page (/resources), and the page is the current, unlinked crumb.
    const crumbs = breadcrumbForPath("/buildings");
    expect(crumbs).toEqual([
      { label: "運用（建物）", href: "/resources" },
      { label: "建物" },
    ]);
  });

  it("matches child routes of a nav item", () => {
    const crumbs = breadcrumbForPath("/points/PT001");
    expect(crumbs.at(-1)).toEqual({ label: "ポイント" });
    expect(crumbs[0].label).toBe("運用（建物）");
  });

  it("returns an empty trail for an unmatched path", () => {
    expect(breadcrumbForPath("/")).toEqual([]);
  });

  it("builds a trail for the newly-linked admin gateway screen (#192)", () => {
    expect(breadcrumbForPath("/admin/gateways")).toEqual([
      { label: "管理", href: "/admin/users" },
      { label: "ゲートウェイ" },
    ]);
  });

  it("builds a platform trail", () => {
    expect(breadcrumbForPath("/platform/config")).toEqual([
      { label: "プラットフォーム", href: "/platform/status" },
      { label: "設定（実効値）" },
    ]);
  });
});
