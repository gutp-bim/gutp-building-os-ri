/**
 * Pure helpers for displaying permission strings (#143). A permission string is
 * `{resourceType}:{resourceId}:{actions}` where resourceType is an abbreviation (b/f/s/d/p/g) and
 * actions is the backend's concatenated abbreviation form (`r`/`w`/`a`, e.g. `rw`, `rwa`); the legacy
 * comma form (`r,w` / `read,write`) and a single full name (`read`) are also accepted — see
 * {@link splitActions}, which mirrors `PermissionHelper.ExpandActions`. Resource ids are
 * SHA-256-prefixed hashes for non-group resources; resolving them to human names is an API concern
 * (a later increment), so display falls back to the raw id here.
 */
export type ParsedPermission = {
  resourceType: string;
  resourceId: string;
  actions: string[];
};

const ABBR_TO_RESOURCE_TYPE: Record<string, string> = {
  b: "building",
  f: "floor",
  s: "space",
  d: "device",
  p: "point",
  g: "group",
};

const ACTION_LABELS: Record<string, string> = {
  r: "読み取り",
  w: "書き込み",
  a: "管理",
  read: "読み取り",
  write: "書き込み",
  admin: "管理",
};

/** Single-char action abbreviations the backend concatenates (e.g. "rw", "rwa"). */
const ACTION_ABBRS = new Set(["r", "w", "a"]);

/**
 * Splits the actions segment, mirroring the backend (`PermissionHelper.ExpandActions`):
 * comma form (`"r,w"` / `"read,write"`) splits on the comma; otherwise a run of known single-char
 * abbreviations (`"rw"`, `"rwa"`) splits per character; anything else (a full name like `"read"`)
 * is kept as one token.
 */
function splitActions(raw: string): string[] {
  if (!raw) return [];
  if (raw.includes(",")) {
    return raw
      .split(",")
      .map((a) => a.trim())
      .filter(Boolean);
  }
  if ([...raw].every((c) => ACTION_ABBRS.has(c))) {
    return [...raw];
  }
  return [raw];
}

const TYPE_COLORS: Record<string, string> = {
  building: "bg-purple-100 text-purple-800",
  floor: "bg-blue-100 text-blue-800",
  space: "bg-green-100 text-green-800",
  device: "bg-orange-100 text-orange-800",
  point: "bg-gray-100 text-gray-800",
  group: "bg-pink-100 text-pink-800",
};

/** Parses a `{type}:{id}:{actions}` permission string, or null when malformed. */
export function parsePermission(permission: string): ParsedPermission | null {
  const parts = permission.split(":");
  if (parts.length !== 3) return null;
  const [rawType, resourceId, rawActions] = parts;
  if (!resourceId) return null;
  return {
    resourceType: ABBR_TO_RESOURCE_TYPE[rawType] ?? rawType,
    resourceId,
    actions: splitActions(rawActions ?? ""),
  };
}

/** Tailwind badge classes for a (full) resource type. */
export function resourceTypeColor(resourceType: string): string {
  return TYPE_COLORS[resourceType] ?? "bg-gray-100 text-gray-800";
}

/** Japanese label for an action code (falls back to the raw code). */
export function actionLabel(action: string): string {
  return ACTION_LABELS[action] ?? action;
}
