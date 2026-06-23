import type { ResourceType } from "./types";

/**
 * Stable key for a resource node, used for tree keys, selection highlight, and the `?sel=` URL param.
 * Points key on their business id (the value used by detail/telemetry routes); everything else on dtId.
 */
export function refKey(ref: {
  type: ResourceType;
  dtId: string;
  id: string;
}): string {
  return `${ref.type}:${ref.type === "point" ? ref.id : ref.dtId}`;
}

/** Deep-link path to a resource's standalone detail page (the existing per-type routes). */
export function detailHref(ref: {
  type: ResourceType;
  dtId: string;
  id: string;
}): string {
  const seg =
    ref.type === "building"
      ? "buildings"
      : ref.type === "floor"
        ? "floors"
        : ref.type === "space"
          ? "spaces"
          : ref.type === "device"
            ? "devices"
            : "points";
  // Points route on their business id; the rest on dtId.
  const param = ref.type === "point" ? ref.id : ref.dtId;
  return `/${seg}/${encodeURIComponent(param)}`;
}

/** Parse a `${type}:${id}` selection key back into its parts, or null when malformed. */
export function parseRefKey(
  key: string,
): { type: ResourceType; id: string } | null {
  const idx = key.indexOf(":");
  if (idx <= 0) return null;
  const type = key.slice(0, idx) as ResourceType;
  const id = key.slice(idx + 1);
  if (!id) return null;
  if (!["building", "floor", "space", "device", "point"].includes(type))
    return null;
  return { type, id };
}
