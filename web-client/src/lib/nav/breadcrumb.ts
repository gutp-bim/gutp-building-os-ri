import { WORKSPACES } from "@/lib/auth/workspaces";
import { isNavItemActive, workspaceForPath } from "./active";
import { NAV_ITEMS, type NavItem } from "./nav-config";

export type Crumb = {
  label: string;
  /** Link target; omitted for the current (last) crumb. */
  href?: string;
};

/**
 * Breadcrumb trail for a path: the workspace it belongs to, then the active nav item (page). Derived
 * from the same nav data as the sidebar (content-as-code), so labels stay in sync. The last crumb has
 * no href (it is the current page). Returns an empty trail for unmatched paths (e.g. "/").
 */
export function breadcrumbForPath(
  pathname: string,
  items: NavItem[] = NAV_ITEMS,
): Crumb[] {
  const ws = workspaceForPath(pathname, items);
  if (!ws) return [];

  const meta = WORKSPACES[ws];
  const active = items
    .filter((item) => isNavItemActive(pathname, item))
    .sort((a, b) => b.href.length - a.href.length)[0];

  // Always [workspace, page]: the workspace and page labels are distinct (even when the page is the
  // workspace landing route, e.g. admin → /admin/users "ユーザー"), so both are informative.
  const trail: Crumb[] = [{ label: meta.label, href: meta.defaultPath }];
  if (active && active.label !== meta.label) {
    trail.push({ label: active.label, href: active.href });
  }

  // The last crumb is the current page → no link (build it without href rather than mutating).
  return trail.map((c, i) => (i === trail.length - 1 ? { label: c.label } : c));
}
