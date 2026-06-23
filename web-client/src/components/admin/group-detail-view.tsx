import { formatDate } from "@/lib/admin/groups-display";
import type { AdminGroupDetail } from "@/lib/admin/types";

/**
 * Pure, read-only group attributes (#143). Resource members are rendered/managed by the detail client
 * via {@link GroupResourceManager}.
 */
export function GroupDetailView({ group }: { group: AdminGroupDetail }) {
  return (
    <div data-testid="group-detail">
      <dl className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        <Field label="名前" value={group.name} />
        <Field label="ID" value={group.id} mono />
        <Field label="説明" value={group.description} />
        <Field label="作成日" value={formatDate(group.createdAt)} />
        <Field label="更新日" value={formatDate(group.updatedAt)} />
      </dl>
    </div>
  );
}

function Field({ label, value, mono }: { label: string; value?: string | null; mono?: boolean }) {
  return (
    <div>
      <dt className="text-sm text-gray-500">{label}</dt>
      <dd className={mono ? "font-mono text-sm" : "font-medium"}>{value || "—"}</dd>
    </div>
  );
}
