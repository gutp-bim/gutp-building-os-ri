"use client";

import {
  controlStatusLabel,
  formatControlRequest,
} from "@/lib/control-audit/mapping";
import { fetchControlAudit } from "@/lib/control-audit/repository";
import type { ControlAuditEntry } from "@/lib/control-audit/types";
import { useEffect, useState } from "react";

/** AA-contrast status fills: success = green, failed = red, pending/in-flight = amber. */
const STATUS_STYLES: Record<ControlAuditEntry["status"], string> = {
  success: "bg-green-100 text-green-800",
  failed: "bg-red-100 text-red-800",
  pending: "bg-amber-100 text-amber-800",
};

/** Injectable loader so the panel is unit-testable offline (defaults to the control-audit façade). */
export type ControlAuditLoader = (pointId: string) => Promise<ControlAuditEntry[]>;

/**
 * Point-detail control history (#162): shows the recorded device-control commands for a point
 * (newest first) with their normalized status. Fills the gap where `point_control_audit` was written
 * but never surfaced in the UI. Read-gated server-side on point read access.
 */
export function ControlAuditHistory({
  pointId,
  load = fetchControlAudit,
}: {
  pointId: string;
  load?: ControlAuditLoader;
}) {
  const [entries, setEntries] = useState<ControlAuditEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setEntries(null);
    setError(null);
    load(pointId)
      .then((e) => active && setEntries(e))
      .catch(
        (e) =>
          active &&
          setError(e instanceof Error ? e.message : "制御履歴の取得に失敗しました"),
      );
    return () => {
      active = false;
    };
  }, [pointId, load]);

  return (
    <section
      data-testid="control-audit-history"
      className="mt-8 rounded-lg border border-gray-200 p-4"
    >
      <h2 className="mb-3 text-lg font-semibold text-gray-800">制御履歴</h2>
      {error ? (
        <p data-testid="control-audit-error" className="text-sm text-red-700">
          {error}
        </p>
      ) : entries === null ? (
        <p className="text-sm text-gray-600">読み込み中…</p>
      ) : entries.length === 0 ? (
        <p data-testid="control-audit-empty" className="text-sm text-gray-600">
          制御履歴はありません。
        </p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-200 text-left text-gray-700">
                <th className="py-2 pr-4 font-medium">日時</th>
                <th className="py-2 pr-4 font-medium">コマンド</th>
                <th className="py-2 pr-4 font-medium">状態</th>
                <th className="py-2 font-medium">完了</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((e) => (
                <tr
                  key={e.controlId}
                  data-testid="control-audit-row"
                  className="border-b border-gray-100"
                >
                  <td className="py-2 pr-4 text-gray-800">
                    {new Date(e.createdAt).toLocaleString("ja-JP")}
                  </td>
                  <td className="py-2 pr-4 text-gray-800">
                    {formatControlRequest(e.request)}
                  </td>
                  <td className="py-2 pr-4">
                    <span
                      data-testid={`control-status-${e.status}`}
                      className={`inline-flex rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_STYLES[e.status]}`}
                    >
                      {controlStatusLabel(e.status)}
                    </span>
                  </td>
                  <td className="py-2 text-gray-600">
                    {e.completedAt
                      ? new Date(e.completedAt).toLocaleString("ja-JP")
                      : "—"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
