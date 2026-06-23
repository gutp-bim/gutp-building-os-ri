"use client";

import { useEffect, useRef, useState } from "react";
import {
  clientStatusBadgeClass,
  clientStatusLabel,
  clientTypeLabel,
  createOidcClient,
  deleteOidcClient,
  fetchOidcClients,
  rotateOidcSecret,
  setOidcClientEnabled,
  type OidcClientSummary,
} from "@/lib/admin/oidc-clients";

/**
 * OIDC クライアントアプリ管理（#324）。一覧・作成・シークレット再生成（一度だけ平文表示）・有効/無効・削除。
 * トラストアンカーは Keycloak の client secret（ゲートウェイの mTLS 証明書とは別物）。管理者のみ。
 */
export function OidcClientsPageClient() {
  const [clients, setClients] = useState<OidcClientSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [revealedSecret, setRevealedSecret] = useState<{ clientId: string; secret: string } | null>(null);
  const [newClientId, setNewClientId] = useState("");
  const [serviceAccount, setServiceAccount] = useState(true);
  const mounted = useRef(true);

  const reload = (signal?: AbortSignal) =>
    fetchOidcClients(signal)
      .then((c) => {
        if (mounted.current) setClients(c);
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

  const run = (op: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    op()
      .then(() => reload())
      .catch((e: Error) => {
        if (mounted.current) setError(e.message);
      })
      .finally(() => {
        if (mounted.current) setBusy(false);
      });
  };

  const onCreate = () => {
    if (!newClientId.trim()) return;
    setBusy(true);
    setError(null);
    createOidcClient({ clientId: newClientId.trim(), serviceAccountsEnabled: serviceAccount })
      .then((created) => {
        if (mounted.current) {
          setRevealedSecret({ clientId: created.client.clientId, secret: created.secret });
          setNewClientId("");
        }
        return reload();
      })
      .catch((e: Error) => {
        if (mounted.current) setError(e.message);
      })
      .finally(() => {
        if (mounted.current) setBusy(false);
      });
  };

  const onRotate = (c: OidcClientSummary) => {
    setBusy(true);
    setError(null);
    rotateOidcSecret(c.id)
      .then((secret) => {
        if (mounted.current) setRevealedSecret({ clientId: c.clientId, secret });
      })
      .catch((e: Error) => {
        if (mounted.current) setError(e.message);
      })
      .finally(() => {
        if (mounted.current) setBusy(false);
      });
  };

  return (
    <div className="container mx-auto px-4 py-8">
      <h1 className="mb-1 text-2xl font-bold">OIDC クライアントアプリ</h1>
      <p className="mb-4 text-sm text-gray-500">
        外部連携アプリ／サービスアカウントの認証情報を管理します。トラストアンカーは Keycloak の
        クライアントシークレット（ゲートウェイの mTLS 証明書とは別系統）です。
      </p>

      {error && (
        <p className="mb-3 text-sm text-red-600" data-testid="oidc-error">
          {error}
        </p>
      )}

      {/* One-time secret reveal */}
      {revealedSecret && (
        <div className="mb-4 rounded border border-amber-300 bg-amber-50 p-3" data-testid="secret-dialog">
          <p className="text-sm font-semibold text-amber-900">
            {revealedSecret.clientId} のシークレット（この画面でのみ表示。再表示はできません）
          </p>
          <code className="mt-1 block break-all rounded bg-white px-2 py-1 text-sm" data-testid="secret-value">
            {revealedSecret.secret}
          </code>
          <button
            type="button"
            className="mt-2 rounded border border-gray-300 px-2 py-0.5 text-xs hover:bg-gray-50"
            onClick={() => setRevealedSecret(null)}
            data-testid="secret-dismiss"
          >
            閉じる（控えました）
          </button>
        </div>
      )}

      {/* Create */}
      <div className="mb-6 flex flex-wrap items-center gap-2" data-testid="create-form">
        <input
          type="text"
          placeholder="新規 clientId"
          value={newClientId}
          onChange={(e) => setNewClientId(e.target.value)}
          className="rounded border border-gray-300 px-2 py-1 text-sm"
          data-testid="new-client-id"
        />
        <label className="flex items-center gap-1 text-sm text-gray-600">
          <input type="checkbox" checked={serviceAccount} onChange={(e) => setServiceAccount(e.target.checked)} />
          サービスアカウント
        </label>
        <button
          type="button"
          disabled={busy || !newClientId.trim()}
          onClick={onCreate}
          className="rounded bg-blue-600 px-3 py-1 text-sm text-white hover:bg-blue-700 disabled:opacity-50"
          data-testid="create-button"
        >
          作成
        </button>
      </div>

      {clients === null ? (
        <p className="text-gray-500">読み込み中…</p>
      ) : clients.length === 0 ? (
        <p className="text-gray-500" data-testid="oidc-empty">クライアントがありません</p>
      ) : (
        <table className="w-full text-left text-sm" data-testid="oidc-table">
          <thead>
            <tr className="border-b border-gray-200 text-gray-500">
              <th className="px-3 py-2 font-medium">clientId</th>
              <th className="px-3 py-2 font-medium">種別</th>
              <th className="px-3 py-2 font-medium">状態</th>
              <th className="px-3 py-2 font-medium">操作</th>
            </tr>
          </thead>
          <tbody>
            {clients.map((c) => (
              <tr key={c.id} className="border-b border-gray-100" data-testid={`oidc-row-${c.id}`}>
                <td className="px-3 py-2 font-mono">{c.clientId}</td>
                <td className="px-3 py-2 text-gray-600">{clientTypeLabel(c)}</td>
                <td className="px-3 py-2">
                  <span className={`rounded px-2 py-0.5 text-xs font-medium ${clientStatusBadgeClass(c)}`}>
                    {clientStatusLabel(c)}
                  </span>
                </td>
                <td className="px-3 py-2">
                  <div className="flex gap-2">
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => onRotate(c)}
                      className="rounded border border-gray-300 px-2 py-0.5 text-xs hover:bg-gray-50 disabled:opacity-50"
                      data-testid={`rotate-${c.id}`}
                    >
                      シークレット再生成
                    </button>
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => run(() => setOidcClientEnabled(c.id, !c.enabled))}
                      className="rounded border border-gray-300 px-2 py-0.5 text-xs hover:bg-gray-50 disabled:opacity-50"
                      data-testid={`toggle-${c.id}`}
                    >
                      {c.enabled ? "無効化" : "有効化"}
                    </button>
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => {
                        if (confirm(`${c.clientId} を削除しますか？`)) run(() => deleteOidcClient(c.id));
                      }}
                      className="rounded border border-red-300 px-2 py-0.5 text-xs text-red-700 hover:bg-red-50 disabled:opacity-50"
                      data-testid={`delete-${c.id}`}
                    >
                      削除
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
