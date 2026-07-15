"use client";

import { useEffect, useRef, useState } from "react";
import { HelpButton } from "@/components/help/help-button";
import {
  bindingLabel,
  fetchGateways,
  resyncGatewayPointList,
  shortRevision,
  type GatewayAdminView,
} from "@/lib/admin/gateways";

/**
 * ゲートウェイ管理（#323）。binding / 接続設定（シークレットはマスク）/ pointlist 同期状態を読み取り専用で
 * 表示し、pointlist 再同期 push を実行する。トラストアンカーは mTLS 証明書（OIDC secret とは別系統）。
 */
export function GatewaysPageClient() {
  const [gateways, setGateways] = useState<GatewayAdminView[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const mounted = useRef(true);

  const reload = (signal?: AbortSignal) =>
    fetchGateways(signal)
      .then((g) => {
        if (mounted.current) setGateways(g);
      })
      .catch((e: Error) => {
        if (e.name !== "AbortError" && mounted.current) setError(e.message);
      });

  useEffect(() => {
    mounted.current = true;
    const controller = new AbortController();
    reload(controller.signal);
    return () => {
      mounted.current = false;
      controller.abort();
    };
  }, []);

  const onResync = (gw: GatewayAdminView) => {
    setBusyId(gw.gatewayId);
    setError(null);
    setNotice(null);
    resyncGatewayPointList(gw.gatewayId)
      .then((rev) => {
        if (mounted.current) setNotice(`${gw.gatewayId} に再同期を通知しました（${shortRevision(rev)}）`);
        return reload();
      })
      .catch((e: Error) => {
        if (mounted.current) setError(e.message);
      })
      .finally(() => {
        if (mounted.current) setBusyId(null);
      });
  };

  return (
    <div className="container mx-auto px-4 py-8">
      <div className="mb-1 flex items-center gap-2">
        <h1 className="text-2xl font-bold">登録済みゲートウェイ</h1>
        <HelpButton helpKey="admin.gateways" />
      </div>
      <p className="mb-4 text-sm text-gray-600">
        binding / 接続設定（シークレットはマスク）と pointlist 同期状態の観測。binding/設定は GitOps が正本のため
        読み取り専用です。アイデンティティは mTLS クライアント証明書（OIDC クライアントの secret とは別系統）。
        これは<strong>登録情報</strong>であり、接続状態（connected / last seen）は表示しません。
      </p>

      {error && <p className="mb-3 text-sm text-red-600" data-testid="gw-error">{error}</p>}
      {notice && <p className="mb-3 text-sm text-green-700" data-testid="gw-notice">{notice}</p>}

      {gateways === null ? (
        <p className="text-gray-600">読み込み中…</p>
      ) : gateways.length === 0 ? (
        <p className="text-gray-600" data-testid="gw-empty">ツインにゲートウェイが登録されていません</p>
      ) : (
        <div className="overflow-x-auto">
        <table className="w-full text-left text-sm" data-testid="gw-table">
          <thead>
            <tr className="border-b border-gray-200 text-gray-700">
              <th className="px-3 py-2 font-medium">gatewayId</th>
              <th className="px-3 py-2 font-medium">binding</th>
              <th className="px-3 py-2 font-medium">point 数</th>
              <th className="px-3 py-2 font-medium">revision</th>
              <th className="px-3 py-2 font-medium">操作</th>
            </tr>
          </thead>
          <tbody>
            {gateways.map((gw) => (
              <tr key={gw.gatewayId} className="border-b border-gray-100" data-testid={`gw-row-${gw.gatewayId}`}>
                <td className="px-3 py-2 font-mono">{gw.gatewayId}</td>
                <td className="px-3 py-2">{bindingLabel(gw.bindingType)}</td>
                <td className="px-3 py-2 text-gray-600">{gw.pointCount}</td>
                <td className="px-3 py-2 font-mono text-xs text-gray-700">{shortRevision(gw.revision)}</td>
                <td className="px-3 py-2">
                  <button
                    type="button"
                    disabled={busyId === gw.gatewayId}
                    onClick={() => onResync(gw)}
                    className="rounded border border-gray-300 px-2 py-0.5 text-xs hover:bg-gray-50 disabled:opacity-50"
                    data-testid={`resync-${gw.gatewayId}`}
                  >
                    pointlist 再同期
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        </div>
      )}
    </div>
  );
}
