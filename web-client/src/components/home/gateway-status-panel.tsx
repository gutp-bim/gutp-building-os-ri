"use client";

import {
  bindingLabel,
  type GatewayAdminView,
  shortRevision,
} from "@/lib/admin/gateways";
import { useEffect, useState } from "react";

/** Injectable gateway fetch so the panel is testable offline (defaults to the admin façade). */
export type GatewaysFetcher = (signal?: AbortSignal) => Promise<GatewayAdminView[]>;

/**
 * Operator-home "登録済みゲートウェイ" panel (#158, naming clarified in #181). Admin-only — the
 * underlying `GET /api/admin/gateways` is admin-gated and there is **no per-gateway up/down signal**,
 * so this shows registration info (binding / point count / point-list revision), NOT live connection
 * state. The heading + note say so explicitly to avoid the "ゲートウェイ状態" misread; real
 * connected/last-seen is tracked in #181 Phase 2. Callers gate on role before rendering it.
 */
export function GatewayStatusPanel({
  fetchGateways,
}: {
  fetchGateways: GatewaysFetcher;
}) {
  const [gateways, setGateways] = useState<GatewayAdminView[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchGateways(controller.signal)
      .then((gs) => setGateways(gs))
      .catch((e) => {
        if (controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : "ゲートウェイの取得に失敗しました");
      });
    return () => controller.abort();
  }, [fetchGateways]);

  return (
    <section data-testid="home-gateway-panel" className="rounded-lg border border-gray-200 p-4">
      <h2 className="text-sm font-semibold text-gray-700">登録済みゲートウェイ</h2>
      <p
        data-testid="home-gateway-panel-note"
        className="mb-3 mt-0.5 text-xs text-gray-600"
      >
        binding / ポイント数 / pointlist リビジョンなどの登録情報です。接続状態（connected /
        last seen）は表示しません。
      </p>
      {error ? (
        <p className="text-sm text-red-700">{error}</p>
      ) : gateways === null ? (
        <p className="text-sm text-gray-600">読み込み中…</p>
      ) : gateways.length === 0 ? (
        <p className="text-sm text-gray-600">ゲートウェイは登録されていません。</p>
      ) : (
        <ul className="divide-y divide-gray-100">
          {gateways.map((g) => (
            <li
              key={g.gatewayId}
              data-testid="home-gateway-row"
              className="flex items-center justify-between py-2 text-sm"
            >
              <div>
                <span className="font-medium text-gray-800">{g.gatewayId}</span>
                <span className="ml-2 text-gray-600">{bindingLabel(g.bindingType)}</span>
              </div>
              <div className="text-gray-600">
                <span>{g.pointCount} ポイント</span>
                <span className="ml-3 font-mono text-xs text-gray-500">
                  {shortRevision(g.revision)}
                </span>
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
