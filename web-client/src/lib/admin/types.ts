import type {
  GroupsControllerGroupDetailResponse,
  GroupsControllerGroupResponse,
  GroupsControllerResourceItemResponse,
  UsersControllerUserResponse,
} from "@/lib/infra/aspida-client/generated/@types";

/**
 * Admin user as returned by `GET /api/Users` and `GET /api/Users/{id}` (UsersController.UserResponse).
 * Admin-gated server-side (403 for non-admins). Aliased to the Swagger-generated type — an API change
 * regenerates through here (#38).
 */
export type AdminUser = UsersControllerUserResponse;

/**
 * One assignable role and what it grants, from `GET /api/Users/roles` (RoleCatalogEntry). Read-only
 * SSOT for the fixed role triad (admin/operator/viewer) and the workspaces each can see (#325).
 * Kept hand-typed with required fields (the generated Swagger type marks everything optional).
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
export type AdminGroup = GroupsControllerGroupResponse;

/** One member of a group (GroupsController.ResourceItemResponse). */
export type AdminGroupResourceItem = GroupsControllerResourceItemResponse;

/** Group detail with members (GroupsController.GroupDetailResponse). */
export type AdminGroupDetail = GroupsControllerGroupDetailResponse;

/** Editable fields of the group create/edit form (#143). */
export interface GroupFormValues {
  id: string;
  name: string;
  description: string;
}
