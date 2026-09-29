import { apiClient } from "@/lib/infra/aspida-client";
import { mutationError, requestError } from "./api-error";
import type { AdminUser, RoleCatalogEntry } from "./types";

/** `GET /api/v1/users` — admin-gated list. */
export async function fetchUsers(signal?: AbortSignal): Promise<AdminUser[]> {
  try {
    return await apiClient().api.v1.users.$get({ config: { signal } });
  } catch (e) {
    throw requestError(e, "users request failed");
  }
}

/** `GET /api/v1/users/roles` — read-only role catalog (admin/operator/viewer + workspaces). */
export async function fetchRoles(
  signal?: AbortSignal,
): Promise<RoleCatalogEntry[]> {
  try {
    return (await apiClient().api.v1.users.roles.$get({
      config: { signal },
    })) as RoleCatalogEntry[];
  } catch (e) {
    throw requestError(e, "roles request failed");
  }
}

/**
 * `PUT /api/v1/users/{id}/enabled` — enable/disable a user (reversible). Returns the updated user.
 * The server returns 409 when the change would lock the actor out or remove the last admin (#325).
 */
export async function setUserEnabled(
  id: string,
  enabled: boolean,
): Promise<AdminUser> {
  try {
    return await apiClient()
      .api.v1.users._id(encodeURIComponent(id))
      .enabled.$put({
        body: { enabled },
      });
  } catch (e) {
    throw mutationError(e, "有効/無効の切り替えに失敗しました");
  }
}

/** `GET /api/v1/users/{id}` — admin-gated detail. */
export async function fetchUser(
  id: string,
  signal?: AbortSignal,
): Promise<AdminUser> {
  try {
    return await apiClient()
      .api.v1.users._id(encodeURIComponent(id))
      .$get({ config: { signal } });
  } catch (e) {
    throw requestError(e, "user request failed");
  }
}

/** `POST /api/v1/users/{id}/permissions` — adds a permission, returns the updated user. */
export async function addUserPermission(
  id: string,
  permission: string,
): Promise<AdminUser> {
  try {
    return await apiClient()
      .api.v1.users._id(encodeURIComponent(id))
      .permissions.$post({
        body: { permission },
      });
  } catch (e) {
    throw mutationError(e, "権限の追加に失敗しました");
  }
}

/** `DELETE /api/v1/users/{id}/permissions` — removes a permission, returns the updated user. */
export async function removeUserPermission(
  id: string,
  permission: string,
): Promise<AdminUser> {
  try {
    return await apiClient()
      .api.v1.users._id(encodeURIComponent(id))
      .permissions.$delete({
        body: { permission },
      });
  } catch (e) {
    throw mutationError(e, "権限の削除に失敗しました");
  }
}
