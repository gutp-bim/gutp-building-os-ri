/**
 * Admin user as returned by `GET /api/Users` and `GET /api/Users/{id}` (UsersController.UserResponse).
 * Admin-gated server-side (403 for non-admins). Hand-typed for now — adding the admin endpoints to the
 * OpenAPI spec and generating these types is a follow-up (#143).
 */
export interface AdminUser {
  id?: string;
  displayName?: string;
  email?: string | null;
  userPrincipalName?: string | null;
  role?: string | null;
  permissions?: string[];
  /** Whether the account can authenticate (Keycloak `enabled`). Defaults to true (#325). */
  enabled?: boolean;
}

/**
 * One assignable role and what it grants, from `GET /api/Users/roles` (RoleCatalogEntry). Read-only
 * SSOT for the fixed role triad (admin/operator/viewer) and the workspaces each can see (#325).
 */
export interface RoleCatalogEntry {
  role: string;
  isAdmin: boolean;
  workspaces: string[];
  description: string;
}

/**
 * Resource group as returned by `GET /api/Groups` (GroupsController.GroupResponse). The list endpoint
 * omits resource items; {@link AdminGroupDetail} (from `GET /api/Groups/{id}`) adds them.
 */
export interface AdminGroup {
  id?: string;
  name?: string;
  description?: string | null;
  createdAt?: string;
  updatedAt?: string;
}

/** One member of a group (GroupsController.ResourceItemResponse). */
export interface AdminGroupResourceItem {
  id?: string;
  resourceType?: string;
  resourceId?: string;
  createdAt?: string;
}

/** Group detail with members (GroupsController.GroupDetailResponse). */
export interface AdminGroupDetail extends AdminGroup {
  resourceItems?: AdminGroupResourceItem[];
}

/** Editable fields of the group create/edit form (#143). */
export interface GroupFormValues {
  id: string;
  name: string;
  description: string;
}
