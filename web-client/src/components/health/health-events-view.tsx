"use client";

/**
 * 永続ヘルスイベント（#455）の一覧ビュー — `/health` の「イベント」タブ。
 *
 * 「データ品質」タブが*いまの状態*を見せるのに対し、こちらは**いつ始まり、いつ終わり、誰が確認したか**を
 * 見せる。判定はサーバ側の評価器が正本で、ここは描画・絞り込み・確認応答の操作だけを担う。
 *
 * - **lifecycle（未解消 / 解消済み）と確認（未確認 / 確認済み）は別の絞り込み**。解消後に確認応答された
 *   行（解消済み + 確認済み）も表示できるよう、1 本の状態に潰さない。
 * - データアクセスは props で注入する（オフラインで単体テストできる）。**条件は URL が正本**で、変更は
 *   `onQueryChange` で親に上げる。
 * - 確認応答できるのは admin / operator（`canAcknowledge`）。権限が無ければボタンを出さない。
 */

import { InlineBanner } from "@/components/ui/inline-banner";
import {
  DEFAULT_EVENTS_QUERY,
  EVENT_KINDS,
  KIND_LABEL,
  SEVERITY_LABEL,
  serializeEventsQuery,
  summarizeDetail,
  type AckFilter,
  type EventsQuery,
  type HealthEvent,
  type HealthEventKind,
  type HealthEventPage,
  type LifecycleFilter,
} from "@/lib/health/events";
import Link from "next/link";
import { useEffect, useMemo, useRef, useState } from "react";

export type HealthEventsLoaders = {
  loadEvents: (query: EventsQuery) => Promise<HealthEventPage>;
  acknowledge: (id: string) => Promise<HealthEvent>;
};

const DASH = "—";

/** 日時の表示。壊れた値は生の文字列ではなくダッシュにする。 */
function formatTime(iso: string | null): string {
  if (!iso) return DASH;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return DASH;
  return d.toLocaleString("ja-JP", {
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

const LIFECYCLES: { value: LifecycleFilter; label: string }[] = [
  { value: "open", label: "未解消" },
  { value: "cleared", label: "解消済み" },
  { value: "", label: "すべて" },
];

const ACKS: { value: AckFilter; label: string }[] = [
  { value: "", label: "すべて" },
  { value: "unacked", label: "未確認" },
  { value: "acked", label: "確認済み" },
];

const SEVERITY_STYLE = {
  critical: "bg-red-100 text-red-800",
  warn: "bg-amber-100 text-amber-800",
} as const;

function Chip({
  active,
  onClick,
  children,
  testId,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
  testId: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      data-testid={testId}
      className={`rounded-full border px-3 py-1 text-xs ${
        active
          ? "border-blue-600 bg-blue-600 text-white"
          : "border-gray-300 bg-white text-gray-700 hover:bg-gray-50"
      }`}
    >
      {children}
    </button>
  );
}

export function HealthEventsView({
  loaders,
  query = DEFAULT_EVENTS_QUERY,
  onQueryChange,
  canAcknowledge,
}: {
  loaders: HealthEventsLoaders;
  query?: EventsQuery;
  onQueryChange?: (next: EventsQuery) => void;
  canAcknowledge: boolean;
}) {
  const [page, setPage] = useState<HealthEventPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [ackError, setAckError] = useState<string | null>(null);
  const [acking, setAcking] = useState<Set<string>>(new Set());

  // 条件は URL 文字列に畳んで比較する（親が同じ内容の別オブジェクトを渡しても取得が走り続けないように）。
  const queryKey = useMemo(() => serializeEventsQuery(query).toString(), [query]);
  const queryRef = useRef(query);
  queryRef.current = query;

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);
    loaders
      .loadEvents(queryRef.current)
      .then((p) => active && setPage(p))
      .catch((e) => {
        if (!active) return;
        setPage(null);
        setError(e instanceof Error ? e.message : "イベントの取得に失敗しました");
      })
      .finally(() => active && setLoading(false));
    return () => {
      active = false;
    };
  }, [loaders, queryKey]);

  const change = (patch: Partial<EventsQuery>) =>
    // 条件を変えたら 1 頁目に戻す（頁位置は旧条件の件数が前提なので）。
    onQueryChange?.({ ...query, ...patch, offset: 0 });
  const toggleKind = (k: HealthEventKind) =>
    change({ kinds: query.kinds.includes(k) ? query.kinds.filter((x) => x !== k) : [...query.kinds, k] });

  const acknowledge = async (id: string) => {
    setAckError(null);
    setAcking((s) => new Set(s).add(id));
    try {
      const updated = await loaders.acknowledge(id);
      // 返ってきた行で置き換える（最初に確認した人が残るので、自分の操作でなくてもそのまま描画できる）。
      // Under 「未確認」 an acknowledged row no longer matches the filter, so it leaves the list (and the count)
      // instead of sitting under the wrong heading until the next fetch.
      setPage((p) => {
        if (!p) return p;
        if (queryRef.current.ack === "unacked" && updated.acknowledgedAt) {
          return { ...p, items: p.items.filter((e) => e.id !== id), total: Math.max(0, p.total - 1) };
        }
        return { ...p, items: p.items.map((e) => (e.id === id ? updated : e)) };
      });
    } catch (e) {
      setAckError(e instanceof Error ? e.message : "確認応答に失敗しました");
    } finally {
      setAcking((s) => {
        const next = new Set(s);
        next.delete(id);
        return next;
      });
    }
  };

  const total = page?.total ?? 0;
  const currentPage = Math.floor(query.offset / query.limit) + 1;
  const lastPage = Math.max(1, Math.ceil(total / query.limit));

  return (
    <div data-testid="health-events-view">
      <div className="mb-3 space-y-2" data-testid="events-filters">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs font-medium text-gray-700">状態</span>
          {LIFECYCLES.map((l) => (
            <Chip
              key={l.label}
              active={query.lifecycle === l.value}
              onClick={() => change({ lifecycle: l.value })}
              testId={`events-lifecycle-${l.value || "all"}`}
            >
              {l.label}
            </Chip>
          ))}
          <span className="ml-3 text-xs font-medium text-gray-700">確認</span>
          {ACKS.map((a) => (
            <Chip
              key={a.label}
              active={query.ack === a.value}
              onClick={() => change({ ack: a.value })}
              testId={`events-ack-${a.value || "all"}`}
            >
              {a.label}
            </Chip>
          ))}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs font-medium text-gray-700">種別</span>
          {EVENT_KINDS.map((k) => (
            <Chip
              key={k}
              active={query.kinds.includes(k)}
              onClick={() => toggleKind(k)}
              testId={`events-kind-${k}`}
            >
              {KIND_LABEL[k]}
            </Chip>
          ))}
        </div>
        <p className="text-xs text-gray-500">
          「状態」はいま起きているか（解消したか）、「確認」は誰かが見たかどうか。2 つは独立しているので、
          解消済みでも確認済みの行があります。
        </p>
      </div>

      {error && (
        <InlineBanner tone="error" testId="events-error">
          {error}
        </InlineBanner>
      )}
      {ackError && (
        <InlineBanner tone="error" onDismiss={() => setAckError(null)} testId="events-ack-error">
          {ackError}
        </InlineBanner>
      )}

      {loading && !page && <p className="text-sm text-gray-600">読み込み中…</p>}

      {page && (
        <>
          <p className="mb-2 text-sm text-gray-700" data-testid="events-total">
            {total} 件
          </p>
          {page.items.length === 0 ? (
            <p className="rounded border border-dashed border-gray-300 p-6 text-center text-sm text-gray-600" data-testid="events-empty">
              該当するイベントはありません。
            </p>
          ) : (
            <div className="overflow-x-auto rounded border border-gray-200">
              <table className="min-w-full text-sm" data-testid="events-table">
                <thead className="bg-gray-50 text-left text-xs text-gray-600">
                  <tr>
                    {["重要度", "種別", "対象", "内容", "発生", "解消", "確認"].map((c) => (
                      <th key={c} className="px-3 py-2 font-medium">
                        {c}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {page.items.map((e) => (
                    <EventRow
                      key={e.id}
                      event={e}
                      canAcknowledge={canAcknowledge}
                      busy={acking.has(e.id)}
                      onAcknowledge={() => acknowledge(e.id)}
                    />
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {lastPage > 1 && (
            <div className="mt-3 flex items-center justify-end gap-2 text-sm">
              <button
                type="button"
                disabled={currentPage <= 1}
                onClick={() => onQueryChange?.({ ...query, offset: query.offset - query.limit })}
                className="rounded border px-3 py-1 disabled:opacity-40"
              >
                前へ
              </button>
              <span data-testid="events-page">
                {currentPage} / {lastPage}
              </span>
              <button
                type="button"
                disabled={currentPage >= lastPage}
                onClick={() => onQueryChange?.({ ...query, offset: query.offset + query.limit })}
                className="rounded border px-3 py-1 disabled:opacity-40"
              >
                次へ
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

function EventRow({
  event: e,
  canAcknowledge,
  busy,
  onAcknowledge,
}: {
  event: HealthEvent;
  canAcknowledge: boolean;
  busy: boolean;
  onAcknowledge: () => void;
}) {
  return (
    <tr className="border-t border-gray-100 align-top" data-testid={`event-row-${e.id}`}>
      <td className="px-3 py-2">
        <span
          className={`inline-flex rounded-full px-2 py-0.5 text-xs font-medium ${SEVERITY_STYLE[e.severity]}`}
          data-testid="event-severity"
        >
          {SEVERITY_LABEL[e.severity]}
        </span>
      </td>
      <td className="px-3 py-2 whitespace-nowrap" data-testid="event-kind">
        {KIND_LABEL[e.kind]}
      </td>
      <td className="px-3 py-2">
        {e.subjectType === "point" ? (
          <Link href={`/points/${encodeURIComponent(e.subjectId)}`} className="text-blue-700 hover:underline">
            {e.subjectName ?? e.subjectId}
          </Link>
        ) : (
          <span>{e.subjectId}</span>
        )}
        <div className="text-xs text-gray-500">
          {e.subjectType === "point" ? `${e.subjectId}${e.buildingName ? ` · ${e.buildingName}` : ""}` : "Gateway"}
        </div>
      </td>
      <td className="px-3 py-2 text-gray-700">{summarizeDetail(e)}</td>
      <td className="px-3 py-2 whitespace-nowrap">{formatTime(e.raisedAt)}</td>
      <td className="px-3 py-2 whitespace-nowrap" data-testid="event-cleared">
        {e.isOpen ? <span className="text-amber-700">未解消</span> : formatTime(e.clearedAt)}
      </td>
      <td className="px-3 py-2" data-testid="event-ack">
        {e.acknowledgedAt ? (
          <span>
            {e.acknowledgedBy ?? "確認済み"}
            <span className="block text-xs text-gray-500">{formatTime(e.acknowledgedAt)}</span>
          </span>
        ) : canAcknowledge ? (
          <button
            type="button"
            onClick={onAcknowledge}
            disabled={busy}
            data-testid="event-ack-button"
            className="rounded bg-blue-600 px-2 py-1 text-xs text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {busy ? "確認中…" : "確認する"}
          </button>
        ) : (
          <span className="text-gray-500">未確認</span>
        )}
      </td>
    </tr>
  );
}
