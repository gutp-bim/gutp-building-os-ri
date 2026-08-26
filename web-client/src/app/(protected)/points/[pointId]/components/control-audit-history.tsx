"use client";

import {
  controlStatusLabel,
  formatControlRequest,
} from "@/lib/control-audit/mapping";
import { fetchControlAudit } from "@/lib/control-audit/repository";
import type { ControlAuditEntry } from "@/lib/control-audit/types";
import { useEffect, useRef, useState } from "react";

/** AA-contrast status fills: success = green, failed = red, pending/in-flight = amber. */
const STATUS_STYLES: Record<ControlAuditEntry["status"], string> = {
  success: "bg-green-100 text-green-800",
  failed: "bg-red-100 text-red-800",
  pending: "bg-amber-100 text-amber-800",
};

/**
 * How long to wait before re-reading a history that still shows a command as 実行中 right after a
 * control settled. Long enough for the result write to commit, short enough that the operator does
 * not read the stale row as the final state.
 */
const PENDING_RETRY_MS = 1_500;

/** Injectable loader so the panel is unit-testable offline (defaults to the control-audit façade). */
export type ControlAuditLoader = (pointId: string) => Promise<ControlAuditEntry[]>;

/**
 * Point-detail control history (#162): shows the recorded device-control commands for a point
 * (newest first) with their normalized status. Read-gated server-side on point read access.
 *
 * Status testids are prefixed `control-audit-status-` to stay distinct from `ControlStatusBar`'s
 * `control-status-`: both render on the point-detail page, and both emit success/failed, so a shared
 * prefix makes any selector for one of them ambiguous.
 */
export function ControlAuditHistory({
  pointId,
  load = fetchControlAudit,
  reloadKey = 0,
}: {
  pointId: string;
  load?: ControlAuditLoader;
  /**
   * Bump to refetch. The audit row for a command is written server-side while the operator is still
   * on the page, so without this the table stays frozen at page-load state and the control they just
   * ran appears to have left no trace (#162).
   */
  reloadKey?: number;
}) {
  const [entries, setEntries] = useState<ControlAuditEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const shownPointId = useRef<string | null>(null);

  useEffect(() => {
    let active = true;
    let retry: ReturnType<typeof setTimeout> | undefined;

    // Keep the current rows visible while refetching. Clearing them collapses the section to a
    // single line, which — with the result bar linking here — moves the anchor out from under the
    // operator mid-scroll. Only a different point genuinely invalidates what is on screen.
    if (shownPointId.current !== pointId) {
      shownPointId.current = pointId;
      setEntries(null);
    }
    setError(null);

    const fetchOnce = (allowRetry: boolean) =>
      load(pointId)
        .then((e) => {
          if (!active) return;
          setEntries(e);
          // The result write lands on ControlAuditResultSubscriber's own subscription, independent
          // of the gRPC stream that just told the browser the control finished — so a refetch fired
          // on that event can beat it and read the row as still 実行中. One retry closes that gap;
          // if it is genuinely still pending afterwards, that is the truth worth showing.
          if (allowRetry && e.some((entry) => entry.status === "pending")) {
            retry = setTimeout(() => active && fetchOnce(false), PENDING_RETRY_MS);
          }
        })
        .catch(
          (e) =>
            active &&
            setError(e instanceof Error ? e.message : "制御履歴の取得に失敗しました"),
        );

    fetchOnce(reloadKey > 0);

    return () => {
      active = false;
      if (retry) clearTimeout(retry);
    };
  }, [pointId, load, reloadKey]);

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
                      data-testid={`control-audit-status-${e.status}`}
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
