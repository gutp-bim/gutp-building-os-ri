/**
 * データ品質（`/health`, #453）のアクセス façade。
 *
 * UI は aspida の生成型を直接触らず、ここが返すドメイン型（{@link HealthPage} /
 * {@link HealthSummary}）だけを見る — `lib/resources` / `lib/telemetry` と同じ方針で、
 * API/Swagger が変わってもここ 1 箇所で吸収する。
 *
 * 役目は 3 つだけ:
 * 1. 検索条件（{@link HealthQuery}）→ クエリ文字列パラメータへの写像（既定値・空配列は送らない）
 * 2. wire のネスト（`freshness` / `alarm` / `gateway`）を 1 行に平らにする
 * 3. 平らにした行を `mapping.ts` の {@link toHealthRow} に渡してドメイン型へ落とす
 *
 * **判定はしない。** 鮮度・値異常・総合ステータスの正本はサーバ側の `PointHealthClassifier`
 * （ADR-0007）で、フロントは再判定も上書きもしない。
 */

import { apiClient } from "@/lib/infra/aspida-client";
import { toHealthRow, type HealthRow } from "./mapping";
import { normalizeHealthQuery, type HealthQuery } from "./query";

/**
 * 最終受信インデックスの状態。`warming`（起動直後で走査中）と `degraded`（一部を読めていない）は
 * どちらも「判定材料が揃っていない」ので、件数を確定値として見せてはいけない。
 */
export type HealthIndexState = "ready" | "warming" | "degraded";

const INDEX_STATES: readonly HealthIndexState[] = [
  "ready",
  "warming",
  "degraded",
];

/** 一覧 1 頁ぶん。`total` は絞り込み後・ページング前の件数。 */
export type HealthPage = {
  rows: HealthRow[];
  total: number;
  limit: number;
  offset: number;
  /** インデックスが揃った状態で判定できたか。false のとき件数は暫定値。 */
  dataComplete: boolean;
  indexState: HealthIndexState;
};

/** 軸別の集計。`suppressed` な値アラームは警報件数に数えない（サーバ側の定義）。 */
export type HealthSummary = {
  totalPoints: number;
  fresh: number;
  stale: number;
  missing: number;
  unknown: number;
  alarmWarn: number;
  alarmCritical: number;
  dataComplete: boolean;
  indexState: HealthIndexState;
};

/** 未指定（undefined）のキーを落とす。aspida には「送らない」を空文字ではなく不在で伝える。 */
function compact<T extends Record<string, unknown>>(o: T): T {
  return Object.fromEntries(
    Object.entries(o).filter(([, v]) => v !== undefined),
  ) as T;
}

function listParams(q: HealthQuery) {
  return compact({
    buildingDtId: q.buildingDtId,
    floorDtId: q.floorDtId,
    deviceDtId: q.deviceDtId,
    gatewayId: q.gatewayId,
    // 空配列は「絞り込まない」なので送らない（送ると空の OR になり 0 件になりかねない）。
    freshness: q.freshness.length > 0 ? [...q.freshness] : undefined,
    alarm: q.alarm.length > 0 ? [...q.alarm] : undefined,
    healthStatus: q.healthStatus.length > 0 ? [...q.healthStatus] : undefined,
    olderThan: q.olderThanSeconds,
    tag: q.tags.length > 0 ? [...q.tags] : undefined,
    q: q.q,
    sort: q.sort,
    limit: q.limit,
    offset: q.offset,
  });
}

/**
 * 集計が受け付けるのはスコープ系の条件だけ。軸別の内訳そのものを返す API なので、
 * 鮮度・値アラームの絞り込みやページングを渡すと「絞り込んだ結果の内訳」になってしまい、
 * チップに出す「全体の中の欠測 47 件」という意味が壊れる。
 */
function summaryParams(q: HealthQuery) {
  return compact({
    buildingDtId: q.buildingDtId,
    floorDtId: q.floorDtId,
    deviceDtId: q.deviceDtId,
    gatewayId: q.gatewayId,
    tag: q.tags.length > 0 ? [...q.tags] : undefined,
    q: q.q,
  });
}

function asRecord(wire: unknown): Record<string, unknown> {
  return typeof wire === "object" && wire !== null && !Array.isArray(wire)
    ? (wire as Record<string, unknown>)
    : {};
}

function countOf(raw: unknown): number {
  return typeof raw === "number" && Number.isFinite(raw) ? raw : 0;
}

function indexStateOf(raw: unknown): HealthIndexState {
  if (typeof raw !== "string") return "ready";
  const found = INDEX_STATES.find((s) => s === raw.trim().toLowerCase());
  // 未知の状態は「確定」に倒す。ここで warming に倒すと、サーバに状態が増えるたび
  // 全画面が暫定表示になってしまう（dataComplete が本来の警告経路）。
  return found ?? "ready";
}

/**
 * wire の 1 行を `toHealthRow` が読める平らな形にする。API は 3 軸をネストして返すが、
 * ドメインの行は軸を潰さないフラットな 1 レコード — 変換はこの 1 箇所だけ。
 */
function flattenItem(wire: unknown): Record<string, unknown> {
  const item = asRecord(wire);
  const freshness = asRecord(item.freshness);
  const alarm = asRecord(item.alarm);
  const gateway = asRecord(item.gateway);

  return {
    pointId: item.pointId,
    name: item.name,
    unit: item.unit,
    freshnessStatus: freshness.status,
    alarmStatus: alarm.status,
    healthStatus: item.healthStatus,
    lastSeen: freshness.lastSeen,
    ageSeconds: freshness.ageSeconds,
    expectedIntervalSeconds: freshness.expectedIntervalSeconds,
    thresholdSeconds: freshness.thresholdSeconds,
    thresholdSource: freshness.thresholdSource,
    missingReason: freshness.reason,
    value: alarm.value,
    violated: alarm.violated,
    gatewayId: gateway.id,
    gatewayConnected: gateway.connected,
    deviceName: item.deviceName,
    spaceName: item.spaceName,
    floorName: item.floorName,
    buildingName: item.buildingName,
    tags: item.tags,
  };
}

/** aspida/axios の拒否からベストエフォートで HTTP ステータスを拾う。 */
function httpStatusOf(e: unknown): number | undefined {
  if (e && typeof e === "object" && "response" in e) {
    const status = (e as { response?: { status?: unknown } }).response?.status;
    if (typeof status === "number") return status;
  }
  return undefined;
}

function requestError(e: unknown, what: string): Error {
  const status = httpStatusOf(e);
  return new Error(`${what}${status !== undefined ? ` (${status})` : ""}`);
}

/** データ品質一覧を 1 頁ぶん取得する。 */
export async function fetchPointHealth(
  query: HealthQuery,
  token?: string,
): Promise<HealthPage> {
  const q = normalizeHealthQuery(query);

  let wire: unknown;
  try {
    wire = await apiClient(token).api.telemetry.health.$get({
      query: listParams(q),
    });
  } catch (e) {
    throw requestError(e, "データ品質の取得に失敗しました");
  }

  const body = asRecord(wire);
  // 1 行が壊れていても頁ごと落とさない（データ品質を見に来た人に何も見せないのが最悪）。
  // ただし pointId を読めない行は詳細への導線が無いので除く。
  const rows = (Array.isArray(body.items) ? body.items : [])
    .map((item) => toHealthRow(flattenItem(item)))
    .filter((row) => row.pointId !== "");

  return {
    rows,
    total: typeof body.total === "number" ? body.total : rows.length,
    limit: typeof body.limit === "number" ? body.limit : q.limit,
    offset: typeof body.offset === "number" ? body.offset : q.offset,
    // 不在は「確定」。欄が無いことを理由に暫定バナーを出すと、常時警告になって意味を失う。
    dataComplete: body.dataComplete !== false,
    indexState: indexStateOf(body.indexState),
  };
}

/** 軸別の集計（チップの件数）を取得する。 */
export async function fetchPointHealthSummary(
  query: HealthQuery,
  token?: string,
): Promise<HealthSummary> {
  const q = normalizeHealthQuery(query);

  let wire: unknown;
  try {
    wire = await apiClient(token).api.telemetry.health.summary.$get({
      query: summaryParams(q),
    });
  } catch (e) {
    throw requestError(e, "データ品質の集計取得に失敗しました");
  }

  const body = asRecord(wire);
  return {
    totalPoints: countOf(body.totalPoints),
    fresh: countOf(body.fresh),
    stale: countOf(body.stale),
    missing: countOf(body.missing),
    unknown: countOf(body.unknown),
    alarmWarn: countOf(body.alarmWarn),
    alarmCritical: countOf(body.alarmCritical),
    dataComplete: body.dataComplete !== false,
    indexState: indexStateOf(body.indexState),
  };
}
