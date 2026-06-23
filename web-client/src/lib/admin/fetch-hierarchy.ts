import { API_BASE_URL, authHeaders } from "./http";
import type { TreeNodeData, TreeResourceType } from "./resource-tree";

/**
 * Bespoke fetch over the digital-twin hierarchy endpoints (root-level, not `/api`) for the
 * tree-browse picker (#143). Buildings/floors/spaces/devices are keyed by `dtId`; points use `id`
 * (mirroring the admin-console picker). Names come from `name`.
 */
type RawNode = { dtId?: string; id?: string; name?: string };

async function getJson(path: string, signal?: AbortSignal): Promise<RawNode[]> {
  const res = await fetch(`${API_BASE_URL}${path}`, { headers: authHeaders(), signal });
  if (!res.ok) throw new Error(`hierarchy request failed: ${res.status}`);
  return (await res.json()) as RawNode[];
}

/** `GET /buildings` → root nodes. */
export async function fetchBuildings(signal?: AbortSignal): Promise<TreeNodeData[]> {
  const rows = await getJson("/buildings", signal);
  return rows.map((b) => ({ type: "building" as const, id: b.dtId ?? "", name: b.name ?? "" }));
}

/** Loads the children (one level down) of a node, or `[]` for a leaf (point). */
export async function fetchChildren(
  parentType: TreeResourceType,
  parentId: string,
  signal?: AbortSignal,
): Promise<TreeNodeData[]> {
  const enc = encodeURIComponent(parentId);
  switch (parentType) {
    case "building": {
      const rows = await getJson(`/floors?buildingDtId=${enc}`, signal);
      return rows.map((f) => ({ type: "floor" as const, id: f.dtId ?? "", name: f.name ?? "" }));
    }
    case "floor": {
      const rows = await getJson(`/spaces?floorDtId=${enc}`, signal);
      return rows.map((s) => ({ type: "space" as const, id: s.dtId ?? "", name: s.name ?? "" }));
    }
    case "space": {
      const rows = await getJson(`/devices?spaceDtId=${enc}`, signal);
      return rows.map((d) => ({ type: "device" as const, id: d.dtId ?? "", name: d.name ?? "" }));
    }
    case "device": {
      const rows = await getJson(`/points?deviceDtId=${enc}`, signal);
      return rows.map((p) => ({ type: "point" as const, id: p.id ?? "", name: p.name ?? "" }));
    }
    default:
      return [];
  }
}
