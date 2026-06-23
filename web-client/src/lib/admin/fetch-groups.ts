import type { AdminGroup, AdminGroupDetail, AdminGroupResourceItem, GroupFormValues } from "./types";
import { API_BASE_URL, authHeaders, mutationError } from "./http";

/** `GET /api/Groups` — admin-gated list (no members). */
export async function fetchGroups(signal?: AbortSignal): Promise<AdminGroup[]> {
  const res = await fetch(`${API_BASE_URL}/api/Groups`, { headers: authHeaders(), signal });
  if (!res.ok) throw new Error(`groups request failed: ${res.status}`);
  return (await res.json()) as AdminGroup[];
}

/** `GET /api/Groups/{id}` — admin-gated detail with members. */
export async function fetchGroup(id: string, signal?: AbortSignal): Promise<AdminGroupDetail> {
  const res = await fetch(`${API_BASE_URL}/api/Groups/${encodeURIComponent(id)}`, {
    headers: authHeaders(),
    signal,
  });
  if (!res.ok) throw new Error(`group request failed: ${res.status}`);
  return (await res.json()) as AdminGroupDetail;
}

/** `POST /api/Groups` — creates a group (admin-gated). Returns the created group. */
export async function createGroup(values: GroupFormValues): Promise<AdminGroup> {
  const res = await fetch(`${API_BASE_URL}/api/Groups`, {
    method: "POST",
    headers: authHeaders(true),
    body: JSON.stringify({
      id: values.id.trim(),
      name: values.name.trim(),
      description: values.description.trim() || undefined,
    }),
  });
  if (!res.ok) throw await mutationError(res, "グループの作成に失敗しました");
  return (await res.json()) as AdminGroup;
}

/** `PUT /api/Groups/{id}` — updates name/description (admin-gated, id immutable). */
export async function updateGroup(
  id: string,
  values: Pick<GroupFormValues, "name" | "description">,
): Promise<void> {
  const res = await fetch(`${API_BASE_URL}/api/Groups/${encodeURIComponent(id)}`, {
    method: "PUT",
    headers: authHeaders(true),
    body: JSON.stringify({
      name: values.name.trim(),
      description: values.description.trim() || undefined,
    }),
  });
  if (!res.ok) throw await mutationError(res, "グループの更新に失敗しました");
}

/** `DELETE /api/Groups/{id}` — deletes a group (admin-gated). */
export async function deleteGroup(id: string): Promise<void> {
  const res = await fetch(`${API_BASE_URL}/api/Groups/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: authHeaders(),
  });
  if (!res.ok) throw await mutationError(res, "グループの削除に失敗しました");
}

/** `POST /api/Groups/{id}/resources` — adds a resource item (raw type/id, no hashing). */
export async function addGroupResource(
  groupId: string,
  resourceType: string,
  resourceId: string,
): Promise<AdminGroupResourceItem> {
  const res = await fetch(`${API_BASE_URL}/api/Groups/${encodeURIComponent(groupId)}/resources`, {
    method: "POST",
    headers: authHeaders(true),
    body: JSON.stringify({ resourceType, resourceId: resourceId.trim() }),
  });
  if (!res.ok) throw await mutationError(res, "リソースの追加に失敗しました");
  return (await res.json()) as AdminGroupResourceItem;
}

/** `DELETE /api/Groups/{id}/resources/{itemId}` — removes a resource item. */
export async function removeGroupResource(groupId: string, itemId: string): Promise<void> {
  const res = await fetch(
    `${API_BASE_URL}/api/Groups/${encodeURIComponent(groupId)}/resources/${encodeURIComponent(itemId)}`,
    { method: "DELETE", headers: authHeaders() },
  );
  if (!res.ok) throw await mutationError(res, "リソースの削除に失敗しました");
}
