import type { BuildingOsRole } from "./claims";

/**
 * A workspace is a role-scoped section of the unified app. The UI shows only the workspaces a
 * user's role grants; switching is purely a UX affordance (the API enforces real authorization).
 */
export type Workspace = "operator" | "admin" | "platform";

export type WorkspaceMeta = {
  id: Workspace;
  /** Short label shown in the workspace switcher. */
  label: string;
  /** Landing path when the workspace is entered. */
  defaultPath: string;
};

export const WORKSPACES: Record<Workspace, WorkspaceMeta> = {
  operator: {
    id: "operator",
    label: "運用（建物）",
    defaultPath: "/resources",
  },
  admin: { id: "admin", label: "管理", defaultPath: "/admin/users" },
  platform: {
    id: "platform",
    label: "プラットフォーム",
    defaultPath: "/platform/status",
  },
};

// Lowest-privilege roles see the operator workspace; admins additionally get admin + platform.
const ROLE_WORKSPACES: Record<BuildingOsRole, Workspace[]> = {
  viewer: ["operator"],
  operator: ["operator"],
  admin: ["operator", "admin", "platform"],
};

/** Workspaces visible to the given role, in display order. Empty when role is unknown. */
export function workspacesForRole(role: BuildingOsRole | null): Workspace[] {
  return role ? ROLE_WORKSPACES[role] : [];
}

/** The workspace a user lands in by default, or null when they have no workspace. */
export function defaultWorkspace(
  role: BuildingOsRole | null,
): Workspace | null {
  return workspacesForRole(role)[0] ?? null;
}

export function canAccessWorkspace(
  role: BuildingOsRole | null,
  ws: Workspace,
): boolean {
  return workspacesForRole(role).includes(ws);
}
