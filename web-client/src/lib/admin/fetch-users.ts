import type { AdminUser, RoleCatalogEntry } from "./types";
import { API_BASE_URL, authHeaders, mutationError } from "./http";

/** `GET /api/Users` — admin-gated list. */
export async function fetchUsers(signal?: AbortSignal): Promise<AdminUser[]> {
  const res = await fetch(`${API_BASE_URL}/api/Users`, { headers: authHeaders(), signal });
  if (!res.ok) throw new Error(`users request failed: ${res.status}`);
  return (await res.json()) as AdminUser[];
}

/** `GET /api/Users/roles` — read-only role catalog (admin/operator/viewer + workspaces). */
export async function fetchRoles(signal?: AbortSignal): Promise<RoleCatalogEntry[]> {
  const res = await fetch(`${API_BASE_URL}/api/Users/roles`, { headers: authHeaders(), signal });
  if (!res.ok) throw new Error(`roles request failed: ${res.status}`);
  return (await res.json()) as RoleCatalogEntry[];
}

/**
 * `PUT /api/Users/{id}/enabled` — enable/disable a user (reversible). Returns the updated user.
 * The server returns 409 when the change would lock the actor out or remove the last admin (#325).
 */
export async function setUserEnabled(id: string, enabled: boolean): Promise<AdminUser> {
  const res = await fetch(`${API_BASE_URL}/api/Users/${encodeURIComponent(id)}/enabled`, {
    method: "PUT",
    headers: authHeaders(true),
    body: JSON.stringify({ enabled }),
  });
  if (!res.ok) throw await mutationError(res, "有効/無効の切り替えに失敗しました");
  return (await res.json()) as AdminUser;
}

/** `GET /api/Users/{id}` — admin-gated detail. */
export async function fetchUser(id: string, signal?: AbortSignal): Promise<AdminUser> {
  const res = await fetch(`${API_BASE_URL}/api/Users/${encodeURIComponent(id)}`, {
    headers: authHeaders(),
    signal,
  });
  if (!res.ok) throw new Error(`user request failed: ${res.status}`);
  return (await res.json()) as AdminUser;
}

/** `POST /api/Users/{id}/permissions` — adds a permission, returns the updated user. */
export async function addUserPermission(id: string, permission: string): Promise<AdminUser> {
  const res = await fetch(`${API_BASE_URL}/api/Users/${encodeURIComponent(id)}/permissions`, {
    method: "POST",
    headers: authHeaders(true),
    body: JSON.stringify({ permission }),
  });
  if (!res.ok) throw await mutationError(res, "権限の追加に失敗しました");
  return (await res.json()) as AdminUser;
}

/** `DELETE /api/Users/{id}/permissions` — removes a permission, returns the updated user. */
export async function removeUserPermission(id: string, permission: string): Promise<AdminUser> {
  const res = await fetch(`${API_BASE_URL}/api/Users/${encodeURIComponent(id)}/permissions`, {
    method: "DELETE",
    headers: authHeaders(true),
    body: JSON.stringify({ permission }),
  });
  if (!res.ok) throw await mutationError(res, "権限の削除に失敗しました");
  return (await res.json()) as AdminUser;
}
