import { apiClient } from "@/lib/infra/aspida-client";
import { mutationError, requestError } from "./api-error";
import type {
  AdminGroup,
  AdminGroupDetail,
  AdminGroupResourceItem,
  GroupFormValues,
} from "./types";

/** `GET /api/v1/groups` — admin-gated list (no members). */
export async function fetchGroups(signal?: AbortSignal): Promise<AdminGroup[]> {
  try {
    return await apiClient().api.v1.groups.$get({ config: { signal } });
  } catch (e) {
    throw requestError(e, "groups request failed");
  }
}

/** `GET /api/v1/groups/{id}` — admin-gated detail with members. */
export async function fetchGroup(
  id: string,
  signal?: AbortSignal,
): Promise<AdminGroupDetail> {
  try {
    return await apiClient()
      .api.v1.groups._id(encodeURIComponent(id))
      .$get({ config: { signal } });
  } catch (e) {
    throw requestError(e, "group request failed");
  }
}

/** `POST /api/v1/groups` — creates a group (admin-gated). Returns the created group. */
export async function createGroup(
  values: GroupFormValues,
): Promise<AdminGroup> {
  try {
    return await apiClient().api.v1.groups.$post({
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

/** `PUT /api/v1/groups/{id}` — updates name/description (admin-gated, id immutable). */
export async function updateGroup(
  id: string,
  values: Pick<GroupFormValues, "name" | "description">,
): Promise<void> {
  try {
    await apiClient()
      .api.v1.groups._id(encodeURIComponent(id))
      .$put({
        body: {
          name: values.name.trim(),
          description: values.description.trim() || undefined,
        },
      });
  } catch (e) {
    throw mutationError(e, "グループの更新に失敗しました");
  }
}

/** `DELETE /api/v1/groups/{id}` — deletes a group (admin-gated). */
export async function deleteGroup(id: string): Promise<void> {
  try {
    await apiClient().api.v1.groups._id(encodeURIComponent(id)).$delete();
  } catch (e) {
    throw mutationError(e, "グループの削除に失敗しました");
  }
}

/** `POST /api/v1/groups/{id}/resources` — adds a resource item (raw type/id, no hashing). */
export async function addGroupResource(
  groupId: string,
  resourceType: string,
  resourceId: string,
): Promise<AdminGroupResourceItem> {
  try {
    return await apiClient()
      .api.v1.groups._id(encodeURIComponent(groupId))
      .resources.$post({
        body: { resourceType, resourceId: resourceId.trim() },
      });
  } catch (e) {
    throw mutationError(e, "リソースの追加に失敗しました");
  }
}

/** `DELETE /api/v1/groups/{id}/resources/{itemId}` — removes a resource item. */
export async function removeGroupResource(
  groupId: string,
  itemId: string,
): Promise<void> {
  try {
    await apiClient()
      .api.v1.groups._id(encodeURIComponent(groupId))
      .resources._itemId(encodeURIComponent(itemId))
      .$delete();
  } catch (e) {
    throw mutationError(e, "リソースの削除に失敗しました");
  }
}
