import { configValueDisplay, isUnset } from "@/lib/system-config/format";
import type { ConfigEntry } from "@/lib/system-config/types";

/**
 * Pure, read-only effective-config table (#147). Secrets render as presence-only ("設定済み" /
 * "未設定") — the value is never present client-side. IaC/ArgoCD remains the source of truth.
 */
export function EffectiveConfigView({ entries }: { entries: ConfigEntry[] }) {
  if (entries.length === 0) {
    return (
      <p className="text-gray-500" data-testid="config-empty">
        設定がありません
      </p>
    );
  }
  return (
    <table className="w-full text-left text-sm" data-testid="config-table">
      <thead>
        <tr className="border-b border-gray-200 text-gray-500">
          <th className="px-3 py-2 font-medium">キー</th>
          <th className="px-3 py-2 font-medium">実効値</th>
          <th className="px-3 py-2 font-medium">種別</th>
        </tr>
      </thead>
      <tbody>
        {entries.map((entry) => (
          <tr key={entry.key} className="border-b border-gray-100" data-testid={`config-row-${entry.key}`}>
            <td className="px-3 py-2 font-mono text-xs">{entry.key}</td>
            <td className={`px-3 py-2 ${isUnset(entry) ? "text-gray-500" : "text-gray-800"}`}>
              {configValueDisplay(entry)}
            </td>
            <td className="px-3 py-2">
              {entry.isSecret ? (
                <span className="rounded bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-800">
                  シークレット
                </span>
              ) : (
                <span className="text-xs text-gray-500">—</span>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
