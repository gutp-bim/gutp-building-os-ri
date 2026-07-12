import Link from "next/link";
import type { AdminUser } from "@/lib/admin/types";
import {
  permissionCount,
  roleBadgeClass,
  roleLabel,
  statusBadgeClass,
  statusLabel,
} from "@/lib/admin/users-display";

/**
 * Pure users list (#143/#325): name / email / role badge / status / permission count, linking to the
 * detail page. Role + enabled status are surfaced so admins can scan access at a glance.
 */
export function UsersTable({ users }: { users: AdminUser[] }) {
  if (users.length === 0) {
    return (
      <p className="text-gray-600" data-testid="users-empty">
        ユーザーがいません
      </p>
    );
  }
  return (
    <div className="overflow-x-auto">
    <table className="w-full text-left text-sm" data-testid="users-table">
      <thead>
        <tr className="border-b border-gray-200 text-gray-700">
          <th className="px-3 py-2 font-medium">名前</th>
          <th className="px-3 py-2 font-medium">メール</th>
          <th className="px-3 py-2 font-medium">ロール</th>
          <th className="px-3 py-2 font-medium">状態</th>
          <th className="px-3 py-2 font-medium">権限数</th>
        </tr>
      </thead>
      <tbody>
        {users.map((u) => (
          <tr key={u.id ?? u.email ?? u.displayName} className="border-b border-gray-100" data-testid={`user-row-${u.id}`}>
            <td className="px-3 py-2">
              {u.id ? (
                <Link href={`/admin/users/${encodeURIComponent(u.id)}`} className="text-blue-600 hover:underline">
                  {u.displayName || u.userPrincipalName || u.id}
                </Link>
              ) : (
                <span>{u.displayName || "—"}</span>
              )}
            </td>
            <td className="px-3 py-2 text-gray-600">{u.email || "—"}</td>
            <td className="px-3 py-2">
              <span className={`rounded px-2 py-0.5 text-xs font-medium ${roleBadgeClass(u.role)}`}>
                {roleLabel(u.role)}
              </span>
            </td>
            <td className="px-3 py-2">
              <span
                className={`rounded px-2 py-0.5 text-xs font-medium ${statusBadgeClass(u)}`}
                data-testid={`user-status-${u.id}`}
              >
                {statusLabel(u)}
              </span>
            </td>
            <td className="px-3 py-2 text-gray-600">{permissionCount(u)}</td>
          </tr>
        ))}
      </tbody>
    </table>
    </div>
  );
}
