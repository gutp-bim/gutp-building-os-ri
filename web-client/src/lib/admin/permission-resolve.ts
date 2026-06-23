import { parsePermission } from "./permissions-display";

/**
 * Resolution of hashed permission resource ids to human names (#143). The backend hashes non-group
 * resource ids; `POST /api/Permissions/resolve` maps a hash back to its original id / type / display
 * name. Group permissions store the raw id (not hashed), so they are not resolved here.
 */
export type ResolvedResourceInfo = {
  originalId?: string;
  resourceType?: string;
  displayName?: string | null;
};

export type ResolvedMap = Record<string, ResolvedResourceInfo>;

/** Collects the unique, non-group resource ids worth resolving from a list of permission strings. */
export function collectResolvableIds(permissions: string[]): string[] {
  const ids = new Set<string>();
  for (const perm of permissions) {
    const parsed = parsePermission(perm);
    if (!parsed) continue;
    if (parsed.resourceType === "group") continue;
    if (parsed.resourceId) ids.add(parsed.resourceId);
  }
  return [...ids];
}

/**
 * Picks the best display for a resource id: the resolved display name, else the resolved original id,
 * else the raw id. When a friendlier label is shown, the raw id is returned as `title` for a tooltip.
 */
export function resolveDisplay(
  resourceId: string,
  resolved?: ResolvedMap,
): { label: string; title?: string } {
  const info = resolved?.[resourceId];
  const friendly = info?.displayName?.trim() || info?.originalId?.trim();
  if (friendly) return { label: friendly, title: resourceId };
  return { label: resourceId };
}
