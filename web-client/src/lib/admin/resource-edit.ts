import { EDIT_RESOURCE_TYPES, type EditResourceType } from "./permission-edit";

/**
 * Pure validation for adding a resource item to a group (#143). Group resource items store the raw
 * `resourceType` + `resourceId` (no hashing, unlike user permissions), so we only require a non-empty
 * id; the type comes from a fixed select. Reuses {@link EDIT_RESOURCE_TYPES}.
 */
export { EDIT_RESOURCE_TYPES };
export type { EditResourceType };

export type ResourceInputValidation = { ok: true } | { ok: false; error: string };

export function validateResourceId(resourceId: string): ResourceInputValidation {
  if (!resourceId.trim()) return { ok: false, error: "リソース ID は必須です" };
  return { ok: true };
}
