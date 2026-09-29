/**
 * Permission strings follow `{resourceType}:{resourceId}:{actions}` where `actions` is a
 * comma-separated list (e.g. `point:<id>:read,write`). This mirrors the API server's
 * AuthorizationContext format. `*` is **not** a wildcard — the API never interpreted it (#505), and
 * admin is decided by the role, not by a permission string.
 *
 * This helper answers a deliberately coarse question — "does the user hold *any* permission of this
 * resourceType + action?" — which is all the nav/shell gating needs. The `resourceId` segment is
 * intentionally ignored here; per-resource scoping is enforced by the API and, if ever needed in the
 * UI, belongs in a separate id-aware check rather than this nav gate.
 *
 * NOTE: UI gating only. The API server re-checks every request; never treat a client-side
 * permission check as the authorization boundary.
 */
export function hasPermission(
  permissions: string[],
  resourceType: string,
  action: string,
): boolean {
  return permissions.some((perm) => {
    const [permType, , permActions] = perm.split(":");
    if (permType === undefined || permActions === undefined) return false;
    if (permType !== resourceType) return false;
    return permActions
      .split(",")
      .map((a) => a.trim())
      .includes(action);
  });
}
