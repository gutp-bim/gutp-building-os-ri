import Link from "next/link";
import { formatDate } from "@/lib/admin/groups-display";
import type { AdminGroup } from "@/lib/admin/types";

/** Pure groups list (#143): name (linking to detail) / id / description / created date. */
export function GroupsTable({ groups }: { groups: AdminGroup[] }) {
  if (groups.length === 0) {
    return (
      <p className="text-gray-600" data-testid="groups-empty">
        グループがありません
      </p>
    );
  }
  return (
    <div className="overflow-x-auto">
    <table className="w-full text-left text-sm" data-testid="groups-table">
      <thead>
        <tr className="border-b border-gray-200 text-gray-700">
          <th className="px-3 py-2 font-medium">名前</th>
          <th className="px-3 py-2 font-medium">ID</th>
          <th className="px-3 py-2 font-medium">説明</th>
          <th className="px-3 py-2 font-medium">作成日</th>
        </tr>
      </thead>
      <tbody>
        {groups.map((g) => (
          <tr key={g.id ?? g.name} className="border-b border-gray-100" data-testid={`group-row-${g.id}`}>
            <td className="px-3 py-2">
              {g.id ? (
                <Link href={`/admin/groups/${encodeURIComponent(g.id)}`} className="text-blue-600 hover:underline">
                  {g.name || g.id}
                </Link>
              ) : (
                <span>{g.name || "—"}</span>
              )}
            </td>
            <td className="px-3 py-2 font-mono text-xs text-gray-700">
              <span className="block max-w-[16rem] truncate" title={g.id || undefined}>{g.id || "—"}</span>
            </td>
            <td className="px-3 py-2 text-gray-600">{g.description || "—"}</td>
            <td className="px-3 py-2 text-gray-600">{formatDate(g.createdAt)}</td>
          </tr>
        ))}
      </tbody>
    </table>
    </div>
  );
}
