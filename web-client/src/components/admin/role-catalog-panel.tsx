import type { RoleCatalogEntry } from "@/lib/admin/types";
import { roleBadgeClass, roleLabel } from "@/lib/admin/users-display";

/**
 * Read-only catalog of the assignable roles (admin/operator/viewer): what each grants — visible
 * workspaces and admin privilege — so admins understand a role change before applying it (#325).
 */
export function RoleCatalogPanel({ roles }: { roles: RoleCatalogEntry[] }) {
  if (roles.length === 0) return null;
  return (
    <section className="rounded border border-gray-200 p-4" data-testid="role-catalog">
      <h2 className="mb-2 text-sm font-semibold text-gray-700">ロールの説明（読み取り専用）</h2>
      <ul className="space-y-2 text-sm">
        {roles.map((r) => (
          <li key={r.role} className="flex flex-col gap-1" data-testid={`role-${r.role}`}>
            <div className="flex items-center gap-2">
              <span className={`rounded px-2 py-0.5 text-xs font-medium ${roleBadgeClass(r.role)}`}>
                {roleLabel(r.role)}
              </span>
              {r.isAdmin && (
                <span className="rounded bg-purple-50 px-1.5 py-0.5 text-xs text-purple-700">管理者権限</span>
              )}
              <span className="text-xs text-gray-500">
                ワークスペース: {r.workspaces.join(" / ")}
              </span>
            </div>
            <p className="text-gray-600">{r.description}</p>
          </li>
        ))}
      </ul>
    </section>
  );
}
