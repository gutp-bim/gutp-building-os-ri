import type { AdminUser } from "./types";

/** Account status as a filterable value. */
export type UserStatusFilter = "all" | "enabled" | "disabled";

/** Role filter: any assignable role or "all". */
export type RoleFilter = "all" | string;

export interface UserFilter {
  role: RoleFilter;
  status: UserStatusFilter;
  /** Free-text match over name / email / id (case-insensitive). */
  query: string;
}

export const EMPTY_USER_FILTER: UserFilter = { role: "all", status: "all", query: "" };

/** A user is enabled unless explicitly `false` (the API omits the flag for legacy/enabled accounts). */
export function isEnabled(user: AdminUser): boolean {
  return user.enabled !== false;
}

export function statusLabel(user: AdminUser): string {
  return isEnabled(user) ? "有効" : "無効";
}

export function statusBadgeClass(user: AdminUser): string {
  return isEnabled(user)
    ? "bg-green-100 text-green-800"
    : "bg-gray-200 text-gray-600";
}

export function roleLabel(role: string | null | undefined): string {
  switch (role) {
    case "admin":
      return "管理者";
    case "operator":
      return "運用";
    case "viewer":
      return "閲覧";
    default:
      return role || "—";
  }
}

export function roleBadgeClass(role: string | null | undefined): string {
  switch (role) {
    case "admin":
      return "bg-purple-100 text-purple-800";
    case "operator":
      return "bg-blue-100 text-blue-800";
    case "viewer":
      return "bg-gray-100 text-gray-700";
    default:
      return "bg-gray-100 text-gray-600";
  }
}

export function permissionCount(user: AdminUser): number {
  return user.permissions?.length ?? 0;
}

/** Apply the list filters. Pure — used by the page client and tested directly (#325). */
export function filterUsers(users: AdminUser[], filter: UserFilter): AdminUser[] {
  const q = filter.query.trim().toLowerCase();
  return users.filter((u) => {
    if (filter.role !== "all" && (u.role ?? "") !== filter.role) return false;
    if (filter.status === "enabled" && !isEnabled(u)) return false;
    if (filter.status === "disabled" && isEnabled(u)) return false;
    if (q) {
      const haystack = [u.displayName, u.email, u.userPrincipalName, u.id]
        .filter(Boolean)
        .join(" ")
        .toLowerCase();
      if (!haystack.includes(q)) return false;
    }
    return true;
  });
}
