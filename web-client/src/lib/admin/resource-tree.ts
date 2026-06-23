/**
 * Pure model for the digital-twin resource hierarchy used by the tree-browse picker (#143):
 * building → floor → space → device → point. Shared by the user-permission editor and the group
 * resource manager so an admin can browse and pick a resource instead of typing its id.
 */
export type TreeResourceType = "building" | "floor" | "space" | "device" | "point";

export type TreeNodeData = {
  type: TreeResourceType;
  /** The id used as the permission / group-resource `resourceId` (dtId, except points use their id). */
  id: string;
  name: string;
};

const CHILD_TYPE: Record<TreeResourceType, TreeResourceType | null> = {
  building: "floor",
  floor: "space",
  space: "device",
  device: "point",
  point: null,
};

/** The child resource type one level down, or null for a leaf (point). */
export function childTypeOf(type: TreeResourceType): TreeResourceType | null {
  return CHILD_TYPE[type];
}

/** Whether a node of this type can be expanded (has a child level). */
export function hasChildren(type: TreeResourceType): boolean {
  return CHILD_TYPE[type] !== null;
}
