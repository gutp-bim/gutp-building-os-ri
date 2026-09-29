import { apiClient } from "@/lib/infra/aspida-client";
import { requestError } from "./api-error";
import type { TreeNodeData, TreeResourceType } from "./resource-tree";

/**
 * Digital-twin hierarchy loaders (`/api/v1/{buildings,floors,…}`) for the tree-browse picker
 * (#143), backed by the generated aspida client (#38). Buildings/floors/spaces/devices are keyed by
 * `dtId`; points use `id` (mirroring the admin-console picker). Names come from `name`.
 */
type RawNode = { dtId?: string; id?: string; name?: string };

async function getRows(load: () => Promise<RawNode[]>): Promise<RawNode[]> {
  try {
    return await load();
  } catch (e) {
    throw requestError(e, "hierarchy request failed");
  }
}

/** `GET /api/v1/buildings` → root nodes. */
export async function fetchBuildings(
  signal?: AbortSignal,
): Promise<TreeNodeData[]> {
  const rows = await getRows(() =>
    apiClient().api.v1.buildings.$get({ config: { signal } }),
  );
  return rows.map((b) => ({
    type: "building" as const,
    id: b.dtId ?? "",
    name: b.name ?? "",
  }));
}

/** Loads the children (one level down) of a node, or `[]` for a leaf (point). */
export async function fetchChildren(
  parentType: TreeResourceType,
  parentId: string,
  signal?: AbortSignal,
): Promise<TreeNodeData[]> {
  const client = apiClient();
  switch (parentType) {
    case "building": {
      const rows = await getRows(() =>
        client.api.v1.floors.$get({
          query: { buildingDtId: parentId },
          config: { signal },
        }),
      );
      return rows.map((f) => ({
        type: "floor" as const,
        id: f.dtId ?? "",
        name: f.name ?? "",
      }));
    }
    case "floor": {
      const rows = await getRows(() =>
        client.api.v1.spaces.$get({
          query: { floorDtId: parentId },
          config: { signal },
        }),
      );
      return rows.map((s) => ({
        type: "space" as const,
        id: s.dtId ?? "",
        name: s.name ?? "",
      }));
    }
    case "space": {
      const rows = await getRows(() =>
        client.api.v1.devices.$get({
          query: { spaceDtId: parentId },
          config: { signal },
        }),
      );
      return rows.map((d) => ({
        type: "device" as const,
        id: d.dtId ?? "",
        name: d.name ?? "",
      }));
    }
    case "device": {
      const rows = await getRows(() =>
        client.api.v1.points.$get({
          query: { deviceDtId: parentId },
          config: { signal },
        }),
      );
      return rows.map((p) => ({
        type: "point" as const,
        id: p.id ?? "",
        name: p.name ?? "",
      }));
    }
    default:
      return [];
  }
}
