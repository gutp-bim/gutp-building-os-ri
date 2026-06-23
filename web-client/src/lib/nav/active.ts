import { NAV_ITEMS, type NavItem } from "./nav-config";
import type { Workspace } from "@/lib/auth/workspaces";

/** True when `pathname` is the nav item's page or a child route of it. */
export function isNavItemActive(pathname: string, item: NavItem): boolean {
  return pathname === item.href || pathname.startsWith(item.href + "/");
}

/**
 * The workspace a path belongs to, found via the longest matching nav href. Returns null when no
 * nav item matches (e.g. `/`), so callers can fall back to the role's default workspace.
 */
export function workspaceForPath(
  pathname: string,
  items: NavItem[] = NAV_ITEMS,
): Workspace | null {
  const match = items
    .filter((item) => isNavItemActive(pathname, item))
    .sort((a, b) => b.href.length - a.href.length)[0];
  return match?.workspace ?? null;
}
