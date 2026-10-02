/**
 * 永続ヘルスイベント（#455, ADR-0005 Phase 2b）のドメイン型・URL 往復・表示文言・アクセス façade。
 *
 * **lifecycle（open / cleared）と確認応答（acked / unacked）は別の軸**で、ここでも 1 本の状態に潰さない
 * （解消後に確認応答された `cleared + acked` も成立する）。UI は aspida の生成型を触らず、ここが返す
 * {@link HealthEvent} だけを見る — `lib/health/repository.ts` と同じ方針。
 */

import { apiClient } from "@/lib/infra/aspida-client";
import type { HealthEventResponse } from "@/lib/infra/aspida-client/generated/@types";
import { formatDurationJa } from "@/lib/telemetry/threshold-explain";
import { missingReasonLabel } from "./mapping";
import { normalizeMissingReason } from "./types";

export type HealthEventKind = "stale" | "missing" | "alarm" | "gateway_offline";
export type HealthEventSeverity = "warn" | "critical";
export type HealthEventSubject = "point" | "gateway";

export const EVENT_KINDS: readonly HealthEventKind[] = [
  "stale",
  "missing",
  "alarm",
  "gateway_offline",
];

/** 発生時のスナップショット。種別ごとに埋まる欄が違う（該当しない欄は null）。 */
export type HealthEventDetail = {
  ageSeconds: number | null;
  thresholdSeconds: number | null;
  lastSeen: string | null;
  reason: string | null;
  value: number | null;
  violated: string | null;
  pointCount: number | null;
};

export type HealthEvent = {
  id: string;
  subjectType: HealthEventSubject;
  subjectId: string;
  subjectName: string | null;
  buildingName: string | null;
  kind: HealthEventKind;
  severity: HealthEventSeverity;
  raisedAt: string;
  clearedAt: string | null;
  isOpen: boolean;
  acknowledgedAt: string | null;
  acknowledgedBy: string | null;
  detail: HealthEventDetail;
};

export type HealthEventPage = {
  items: HealthEvent[];
  total: number;
  limit: number;
  offset: number;
};

export type LifecycleFilter = "" | "open" | "cleared";
export type AckFilter = "" | "acked" | "unacked";

export type EventsQuery = {
  lifecycle: LifecycleFilter;
  ack: AckFilter;
  kinds: HealthEventKind[];
  /** 絞り込み対象（Point 詳細の「この Point の直近イベント」が使う）。 */
  subjectType?: HealthEventSubject;
  subjectId?: string;
  limit: number;
  offset: number;
};

export const EVENTS_PAGE_SIZE = 50;

/** 既定は「いま起きていること」＝未解消。履歴は lifecycle を切り替えて見る。 */
export const DEFAULT_EVENTS_QUERY: EventsQuery = {
  lifecycle: "open",
  ack: "",
  kinds: [],
  limit: EVENTS_PAGE_SIZE,
  offset: 0,
};

// ── URL ────────────────────────────────────────────────────────────────────

// `lifecycle=all` は「両方」を明示する（空だと既定の open と区別できない）。
const ALL = "all";

function pick<T extends string>(raw: string | null, allowed: readonly T[]): T | null {
  const v = (raw ?? "").trim().toLowerCase();
  return (allowed as readonly string[]).includes(v) ? (v as T) : null;
}

/** URL から条件を読む。未知の値は既定に倒す（共有 URL の表記ゆれで画面を落とさない）。 */
export function parseEventsQuery(params: URLSearchParams): EventsQuery {
  const raw = params.get("lifecycle");
  const lifecycle: LifecycleFilter =
    raw === null ? DEFAULT_EVENTS_QUERY.lifecycle : raw.toLowerCase() === ALL ? "" : (pick(raw, ["open", "cleared"]) ?? DEFAULT_EVENTS_QUERY.lifecycle);

  const kinds: HealthEventKind[] = [];
  for (const k of params.getAll("kind").flatMap((v) => v.split(","))) {
    const kind = pick(k, EVENT_KINDS);
    if (kind && !kinds.includes(kind)) kinds.push(kind);
  }

  const page = Number.parseInt(params.get("page") ?? "1", 10);
  return {
    lifecycle,
    ack: pick(params.get("ack"), ["acked", "unacked"]) ?? "",
    kinds,
    limit: EVENTS_PAGE_SIZE,
    offset: Number.isFinite(page) && page > 1 ? (page - 1) * EVENTS_PAGE_SIZE : 0,
  };
}

/** 条件を URL に書く。既定値は出さない。`view=events` は呼び出し側（タブ）が付ける。 */
export function serializeEventsQuery(q: EventsQuery): URLSearchParams {
  const params = new URLSearchParams();
  if (q.lifecycle !== DEFAULT_EVENTS_QUERY.lifecycle) params.set("lifecycle", q.lifecycle === "" ? ALL : q.lifecycle);
  if (q.ack) params.set("ack", q.ack);
  for (const k of q.kinds) params.append("kind", k);
  const page = Math.floor(q.offset / EVENTS_PAGE_SIZE) + 1;
  if (page > 1) params.set("page", String(page));
  return params;
}

// ── 文言 ───────────────────────────────────────────────────────────────────

export const KIND_LABEL: Record<HealthEventKind, string> = {
  stale: "鮮度切れ",
  missing: "欠測",
  alarm: "値異常",
  gateway_offline: "Gateway 切断",
};

export const SEVERITY_LABEL: Record<HealthEventSeverity, string> = {
  warn: "警告",
  critical: "重大",
};

/** 1 行の要約。発生時のスナップショットから、運用者が次に見るべきことを短く言う。 */
export function summarizeDetail(e: HealthEvent): string {
  const d = e.detail;
  switch (e.kind) {
    case "stale": {
      const age = d.ageSeconds !== null ? `最終受信から ${formatDurationJa(d.ageSeconds)}` : "最終受信が古い";
      return d.thresholdSeconds !== null ? `${age}（閾値 ${formatDurationJa(d.thresholdSeconds)}）` : age;
    }
    case "missing":
      return missingReasonLabel(normalizeMissingReason(d.reason) ?? "unknown");
    case "alarm":
      return `${d.value !== null ? `値 ${d.value}` : "値異常"}${d.violated ? `（${d.violated}）` : ""}`;
    case "gateway_offline":
      return d.pointCount !== null ? `配下 ${d.pointCount} Point が影響` : "接続が切れています";
  }
}

// ── 変換 ───────────────────────────────────────────────────────────────────

function oneOf<T extends string>(v: string | undefined, allowed: readonly T[], fallback: T): T {
  return (allowed as readonly string[]).includes(v ?? "") ? (v as T) : fallback;
}

export function toHealthEvent(r: HealthEventResponse): HealthEvent {
  const d = r.detail;
  return {
    id: r.id ?? "",
    subjectType: oneOf(r.subjectType, ["point", "gateway"], "point"),
    subjectId: r.subjectId ?? "",
    subjectName: r.subjectName ?? null,
    buildingName: r.buildingName ?? null,
    kind: oneOf(r.kind, EVENT_KINDS, "stale"),
    severity: oneOf(r.severity, ["warn", "critical"], "warn"),
    raisedAt: r.raisedAt ?? "",
    clearedAt: r.clearedAt ?? null,
    isOpen: r.isOpen ?? r.clearedAt == null,
    acknowledgedAt: r.acknowledgedAt ?? null,
    acknowledgedBy: r.acknowledgedBy ?? null,
    detail: {
      ageSeconds: d?.ageSeconds ?? null,
      thresholdSeconds: d?.thresholdSeconds ?? null,
      lastSeen: d?.lastSeen ?? null,
      reason: d?.reason ?? null,
      value: d?.value ?? null,
      violated: d?.violated ?? null,
      pointCount: d?.pointCount ?? null,
    },
  };
}

// ── アクセス ───────────────────────────────────────────────────────────────

export async function fetchHealthEvents(q: EventsQuery, token?: string): Promise<HealthEventPage> {
  const res = await apiClient(token).api.v1.health.events.$get({
    query: {
      lifecycle: q.lifecycle || undefined,
      ack: q.ack || undefined,
      kind: q.kinds.length > 0 ? q.kinds : undefined,
      subjectType: q.subjectType,
      subjectId: q.subjectId,
      limit: q.limit,
      offset: q.offset,
    },
  });
  return {
    items: (res.items ?? []).map(toHealthEvent).filter((e) => e.id !== ""),
    total: res.total ?? 0,
    limit: res.limit ?? q.limit,
    offset: res.offset ?? q.offset,
  };
}

/** 確認応答（冪等）。最初に確認した人が残るので、返ってきた行をそのまま描画に使う。 */
export async function acknowledgeHealthEvent(id: string, token?: string): Promise<HealthEvent> {
  const res = await apiClient(token).api.v1.health.events._id(id).ack.$post();
  return toHealthEvent(res);
}
