import type { AdminGroup, AdminGroupDetail, AdminGroupResourceItem, GroupFormValues } from "./types";
import { apiClient } from "@/lib/infra/aspida-client";
import { mutationError, requestError } from "./api-error";

/** `GET /api/Groups` — admin-gated list (no members). */
export async function fetchGroups(signal?: AbortSignal): Promise<AdminGroup[]> {
  try {
    return await apiClient().api.Groups.$get({ config: { signal } });
  } catch (e) {
    throw requestError(e, "groups request failed");
  }
}

/** `GET /api/Groups/{id}` — admin-gated detail with members. */
export async function fetchGroup(id: string, signal?: AbortSignal): Promise<AdminGroupDetail> {
  try {
    return await apiClient().api.Groups._id(encodeURIComponent(id)).$get({ config: { signal } });
  } catch (e) {
    throw requestError(e, "group request failed");
  }
}

/** `POST /api/Groups` — creates a group (admin-gated). Returns the created group. */
export async function createGroup(values: GroupFormValues): Promise<AdminGroup> {
  try {
    return await apiClient().api.Groups.$post({
      body: {
        id: values.id.trim(),
        name: values.name.trim(),
        description: values.description.trim() || undefined,
      },
    });
  } catch (e) {
    throw mutationError(e, "グループの作成に失敗しました");
  }
}

/** `PUT /api/Groups/{id}` — updates name/description (admin-gated, id immutable). */
export async function updateGroup(
  id: string,
  values: Pick<GroupFormValues, "name" | "description">,
): Promise<void> {
  try {
    await apiClient().api.Groups._id(encodeURIComponent(id)).$put({
      body: {
        name: values.name.trim(),
        description: values.description.trim() || undefined,
      },
    });
  } catch (e) {
    throw mutationError(e, "グループの更新に失敗しました");
  }
}

/** `DELETE /api/Groups/{id}` — deletes a group (admin-gated). */
export async function deleteGroup(id: string): Promise<void> {
  try {
    await apiClient().api.Groups._id(encodeURIComponent(id)).$delete();
  } catch (e) {
    throw mutationError(e, "グループの削除に失敗しました");
  }
}

/** `POST /api/Groups/{id}/resources` — adds a resource item (raw type/id, no hashing). */
export async function addGroupResource(
  groupId: string,
  resourceType: string,
  resourceId: string,
): Promise<AdminGroupResourceItem> {
  try {
    return await apiClient().api.Groups._id(encodeURIComponent(groupId)).resources.$post({
      body: { resourceType, resourceId: resourceId.trim() },
    });
  } catch (e) {
    throw mutationError(e, "リソースの追加に失敗しました");
  }
}

/** `DELETE /api/Groups/{id}/resources/{itemId}` — removes a resource item. */
export async function removeGroupResource(groupId: string, itemId: string): Promise<void> {
  try {
    await apiClient()
      .api.Groups._id(encodeURIComponent(groupId))
      .resources._itemId(encodeURIComponent(itemId))
      .$delete();
  } catch (e) {
    throw mutationError(e, "リソースの削除に失敗しました");
  }
}
