import type { ResolvedMap } from "./permission-resolve";
import { API_BASE_URL, authHeaders } from "./http";

/**
 * `POST /api/Permissions/resolve` — resolves hashed resource ids to original id / type / display name
 * (admin-gated). Returns a map keyed by hashed id; unresolved ids are simply absent.
 */
export async function resolvePermissionIds(
  hashedIds: string[],
  signal?: AbortSignal,
): Promise<ResolvedMap> {
  if (hashedIds.length === 0) return {};
  const res = await fetch(`${API_BASE_URL}/api/Permissions/resolve`, {
    method: "POST",
    headers: authHeaders(true),
    body: JSON.stringify(hashedIds),
    signal,
  });
  if (!res.ok) throw new Error(`resolve request failed: ${res.status}`);
  return (await res.json()) as ResolvedMap;
}
