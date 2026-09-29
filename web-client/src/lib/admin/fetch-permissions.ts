import { apiClient } from "@/lib/infra/aspida-client";
import { requestError } from "./api-error";
import type { ResolvedMap } from "./permission-resolve";

/**
 * `POST /api/Permissions/resolve` — resolves hashed resource ids to original id / type / display name
 * (admin-gated). Returns a map keyed by hashed id; unresolved ids are simply absent.
 */
export async function resolvePermissionIds(
  hashedIds: string[],
  signal?: AbortSignal,
): Promise<ResolvedMap> {
  if (hashedIds.length === 0) return {};
  try {
    return await apiClient().api.v1.Permissions.resolve.$post({
      body: hashedIds,
      config: { signal },
    });
  } catch (e) {
    throw requestError(e, "resolve request failed");
  }
}
