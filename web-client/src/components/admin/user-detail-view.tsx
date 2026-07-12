import type { AdminUser } from "@/lib/admin/types";

/** Pure, read-only user attributes (#143). Permissions are rendered/edited by the detail client. */
export function UserDetailView({ user }: { user: AdminUser }) {
  return (
    <div data-testid="user-detail">
      <dl className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        <Field label="名前" value={user.displayName} />
        <Field label="メール" value={user.email} />
        <Field label="UPN" value={user.userPrincipalName} />
        <Field label="ロール" value={user.role} />
      </dl>
    </div>
  );
}

function Field({ label, value }: { label: string; value?: string | null }) {
  return (
    <div>
      <dt className="text-sm text-gray-600">{label}</dt>
      <dd className="font-medium">{value || "—"}</dd>
    </div>
  );
}
