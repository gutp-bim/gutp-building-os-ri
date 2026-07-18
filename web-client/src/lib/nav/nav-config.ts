import { hasPermission } from "@/lib/auth/permissions";
import type { Workspace } from "@/lib/auth/workspaces";

/**
 * A sidebar navigation item. Content-as-code: the nav is data, gated by workspace and optionally
 * by a permission (resourceType + action checked against the user's permission strings).
 */
export type NavItem = {
  label: string;
  href: string;
  workspace: Workspace;
  /** Optional permission gate. When set, the item is hidden unless the user holds it. */
  permission?: { resourceType: string; action: string };
  /**
   * Hidden from the sidebar but still used for workspace/breadcrumb resolution. Used for deep-link
   * detail routes (e.g. /buildings/[id]) that have no sidebar entry of their own.
   */
  hidden?: boolean;
};

export const NAV_ITEMS: NavItem[] = [
  // operator workspace — the per-type list links (/floors, /spaces, /points had no list page) are
  // replaced by a single tree+search resource explorer (#UI-improve). Deep-link detail routes
  // (/buildings/[id] etc.) are unchanged.
  // "ホーム" (#158): non-disruptive freshness/attention landing; does not change the login redirect.
  { label: "ホーム", href: "/home", workspace: "operator" },
  { label: "リソース", href: "/resources", workspace: "operator" },
  // "マイリソース" was consolidated into /resources (#195): the route now redirects there, so it is
  // hidden from the sidebar but kept for workspace/breadcrumb resolution (like the other retired lists).
  { label: "マイリソース", href: "/my-resources", workspace: "operator", hidden: true },
  // Hidden: deep-link detail routes keep their workspace/breadcrumb mapping without a sidebar entry.
  { label: "建物", href: "/buildings", workspace: "operator", hidden: true },
  { label: "フロア", href: "/floors", workspace: "operator", hidden: true },
  { label: "スペース", href: "/spaces", workspace: "operator", hidden: true },
  { label: "デバイス", href: "/devices", workspace: "operator", hidden: true },
  { label: "ポイント", href: "/points", workspace: "operator", hidden: true },
  // admin workspace (screens migrated in #143)
  { label: "ユーザー", href: "/admin/users", workspace: "admin" },
  { label: "グループ", href: "/admin/groups", workspace: "admin" },
  // Shipped admin screens that previously had no sidebar entry (#192): reachable only by URL before,
  // now surfaced so breadcrumb/workspace resolution and active-highlight work. admin-role only via
  // the workspace gate (no per-item permission needed, matching users/groups).
  { label: "ゲートウェイ", href: "/admin/gateways", workspace: "admin" },
  { label: "OIDC クライアント", href: "/admin/oidc-clients", workspace: "admin" },
  { label: "Twin 管理", href: "/admin/twin", workspace: "admin" },
  // platform workspace (screens added in #146 / #147 / #148)
  {
    label: "システム稼働状態",
    href: "/platform/status",
    workspace: "platform",
  },
  { label: "設定（実効値）", href: "/platform/config", workspace: "platform" },
  { label: "アプリ設定", href: "/platform/settings", workspace: "platform" },
];

/**
 * The nav items visible in a workspace for a user with the given permissions. Items without a
 * permission gate are always shown; gated items require a matching permission.
 */
export function visibleNavItems(
  workspace: Workspace,
  permissions: string[],
  items: NavItem[] = NAV_ITEMS,
): NavItem[] {
  return items.filter((item) => {
    if (item.hidden) return false;
    if (item.workspace !== workspace) return false;
    if (!item.permission) return true;
    return hasPermission(
      permissions,
      item.permission.resourceType,
      item.permission.action,
    );
  });
}
