/**
 * Pure helpers for building a permission string to add to a user (#143). Mirrors the picker format
 * `{typeAbbr}:{resourceId}:{concatActionAbbr}` (e.g. `d:rawId:rw`). The API canonicalises further on
 * `POST /api/Users/{id}/permissions` — it hashes non-group resource ids and re-abbreviates actions —
 * so we send a raw resource id and let the server hash it.
 */
export const EDIT_RESOURCE_TYPES = [
  "building",
  "floor",
  "space",
  "device",
  "point",
  "group",
] as const;
export type EditResourceType = (typeof EDIT_RESOURCE_TYPES)[number];

export const EDIT_ACTIONS = ["read", "write", "admin"] as const;
export type EditAction = (typeof EDIT_ACTIONS)[number];

const TYPE_TO_ABBR: Record<EditResourceType, string> = {
  building: "b",
  floor: "f",
  space: "s",
  device: "d",
  point: "p",
  group: "g",
};

const ACTION_TO_ABBR: Record<EditAction, string> = {
  read: "r",
  write: "w",
  admin: "a",
};

export type PermissionInput = {
  resourceType: EditResourceType;
  resourceId: string;
  actions: EditAction[];
};

export type PermissionInputValidation = { ok: true } | { ok: false; error: string };

export function validatePermissionInput(input: {
  resourceId: string;
  actions: EditAction[];
}): PermissionInputValidation {
  if (!input.resourceId.trim()) return { ok: false, error: "リソース ID は必須です" };
  if (input.actions.length === 0) return { ok: false, error: "アクションを 1 つ以上選択してください" };
  return { ok: true };
}

/**
 * Builds `{typeAbbr}:{resourceId}:{actionAbbr...}`. Actions are emitted in canonical r/w/a order so
 * the same selection always produces the same string. The resource id is trimmed but not hashed.
 */
export function buildPermissionString(input: PermissionInput): string {
  const abbrType = TYPE_TO_ABBR[input.resourceType];
  const abbrActions = EDIT_ACTIONS.filter((a) => input.actions.includes(a))
    .map((a) => ACTION_TO_ABBR[a])
    .join("");
  return `${abbrType}:${input.resourceId.trim()}:${abbrActions}`;
}
