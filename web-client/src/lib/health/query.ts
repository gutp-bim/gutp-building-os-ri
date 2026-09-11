/**
 * `/health`（データ健全性一覧, #452）の検索条件を URL クエリと相互変換する純粋モジュール。
 *
 * この画面の絞り込みは **URL が正本**（共有・ブックマーク・リロードで再現できることが運用要件）。
 * したがって「URL → 条件」も「条件 → URL」もここ 1 箇所で完結させ、画面側は状態を持たない。
 *
 * 方針:
 * - **壊れた入力で落ちない。** 未知の enum 値・数値でない limit・負の offset は捨てるか丸める。
 *   人手で編集された URL や、古いブックマークが来ても最悪「既定の一覧」に落ちるだけにする。
 * - **既定値は出力しない。** URL を短く保ち、同じ条件が常に同じ URL になる（round-trip が安定する）。
 * - **多値は 1 パラメータにカンマ区切り**（`freshness=stale,missing`）。ただし `tag` だけは
 *   カンマを含みうる自由文字列なので繰り返しパラメータで出す。
 *
 * サーバへ渡すのは `repository.ts`（後続フェーズ）の役目で、ここは I/O を持たない。
 */

import {
  normalizeAlarmStatus,
  normalizeFreshnessStatus,
  normalizeHealthStatus,
  type AlarmStatus,
  type FreshnessStatus,
  type HealthStatus,
} from "./types";

/** 並び順。`worst` は重篤度（HealthStatus の宣言順）降順、既定。 */
export type HealthSort = "worst" | "lastSeen" | "name";

const SORTS: readonly HealthSort[] = ["worst", "lastSeen", "name"];

export type HealthQuery = {
  freshness: FreshnessStatus[];
  alarm: AlarmStatus[];
  healthStatus: HealthStatus[];
  /** 「この秒数より古い」= 最終受信からの経過秒数の下限。未指定なら絞り込まない。 */
  olderThanSeconds?: number;
  buildingDtId?: string;
  floorDtId?: string;
  deviceDtId?: string;
  gatewayId?: string;
  /** `customTags`（AND 条件）。カンマを含みうるので分解しない。 */
  tags: string[];
  /** 名前・Point ID の部分一致。 */
  q?: string;
  sort: HealthSort;
  limit: number;
  offset: number;
};

export const DEFAULT_LIMIT = 100;
export const MAX_LIMIT = 500;
export const MIN_LIMIT = 1;

export const DEFAULT_HEALTH_QUERY: HealthQuery = {
  freshness: [],
  alarm: [],
  healthStatus: [],
  tags: [],
  sort: "worst",
  limit: DEFAULT_LIMIT,
  offset: 0,
};

const DURATION_UNIT_SECONDS: Record<string, number> = {
  s: 1,
  m: 60,
  h: 3600,
  d: 86400,
};

/**
 * `45s` / `30m` / `1h` / `2d` / 単位なしの秒数を **秒** に変換する。単位は大文字も可、前後の
 * 空白は許容。小数は秒に floor する（`1.5m` → 90、`0.5s` → 0）。
 *
 * 負値・単位違い・`Infinity` など解釈できない入力は `null`。「読めなかった」を 0 に倒すと
 * 「0 秒より古い = 全件」という意図しない絞り込みになるため、必ず null で返して呼び出し側に
 * 「未指定」を選ばせる。
 */
export function parseDuration(raw: string | null | undefined): number | null {
  if (typeof raw !== "string") return null;
  const matched = /^(\d+(?:\.\d+)?)\s*([smhd])?$/i.exec(raw.trim());
  if (matched === null) return null;

  const value = Number(matched[1]);
  if (!Number.isFinite(value)) return null;
  const unit = matched[2]?.toLowerCase() ?? "s";
  return Math.floor(value * DURATION_UNIT_SECONDS[unit]);
}

/** 順序を保ったまま重複を除く。 */
function dedupe<T>(values: T[]): T[] {
  return [...new Set(values)];
}

/**
 * 多値パラメータを読む。繰り返し指定（`?freshness=a&freshness=b`）とカンマ区切り
 * （`?freshness=a,b`）の両方、およびその混在を受け付ける。未知の値と空文字は捨てる
 * （絞り込みが 1 つ壊れているだけで一覧全体を出せなくしない）。
 */
function readEnumList<T extends string>(
  params: URLSearchParams,
  key: string,
  normalize: (raw: unknown) => T | null,
): T[] {
  const values = params
    .getAll(key)
    .flatMap((v) => v.split(","))
    .map(normalize)
    .filter((v): v is T => v !== null);
  return dedupe(values);
}

/** 空白のみ・空文字は「未指定」として扱う。 */
function readScalar(params: URLSearchParams, key: string): string | undefined {
  return trimToUndefined(params.get(key));
}

function trimToUndefined(raw: string | null | undefined): string | undefined {
  const trimmed = raw?.trim() ?? "";
  return trimmed === "" ? undefined : trimmed;
}

function clampLimit(raw: number | undefined): number {
  if (raw === undefined || !Number.isFinite(raw)) return DEFAULT_LIMIT;
  return Math.min(MAX_LIMIT, Math.max(MIN_LIMIT, Math.floor(raw)));
}

function clampOffset(raw: number | undefined): number {
  if (raw === undefined || !Number.isFinite(raw)) return 0;
  return Math.max(0, Math.floor(raw));
}

function readNumber(params: URLSearchParams, key: string): number | undefined {
  const raw = params.get(key);
  if (raw === null || raw.trim() === "") return undefined;
  const value = Number(raw);
  return Number.isFinite(value) ? value : undefined;
}

/** 経過秒数の下限は正の値のときだけ意味を持つ（0 以下は「絞り込まない」と同義）。 */
function usableSeconds(raw: number | null | undefined): number | undefined {
  return typeof raw === "number" && Number.isFinite(raw) && raw > 0
    ? Math.floor(raw)
    : undefined;
}

function normalizeTags(tags: string[] | undefined): string[] {
  return dedupe((tags ?? []).map((t) => t.trim()).filter((t) => t !== ""));
}

function normalizeSort(raw: unknown): HealthSort {
  if (typeof raw !== "string") return DEFAULT_HEALTH_QUERY.sort;
  const found = SORTS.find((s) => s.toLowerCase() === raw.trim().toLowerCase());
  return found ?? DEFAULT_HEALTH_QUERY.sort;
}

/** URL クエリを検索条件に読み解く。読めない値は捨てるか既定に倒すので、決して throw しない。 */
export function parseHealthQuery(params: URLSearchParams): HealthQuery {
  return {
    freshness: readEnumList(params, "freshness", normalizeFreshnessStatus),
    alarm: readEnumList(params, "alarm", normalizeAlarmStatus),
    healthStatus: readEnumList(params, "healthStatus", normalizeHealthStatus),
    olderThanSeconds: usableSeconds(parseDuration(params.get("olderThan"))),
    buildingDtId: readScalar(params, "buildingDtId"),
    floorDtId: readScalar(params, "floorDtId"),
    deviceDtId: readScalar(params, "deviceDtId"),
    gatewayId: readScalar(params, "gatewayId"),
    tags: normalizeTags(params.getAll("tag")),
    q: readScalar(params, "q"),
    sort: normalizeSort(params.get("sort")),
    limit: clampLimit(readNumber(params, "limit")),
    offset: clampOffset(readNumber(params, "offset")),
  };
}

/**
 * 検索条件を正規化する（重複除去・trim・clamp・未知値の除去）。`parseHealthQuery` が URL から
 * 作る値と、画面が組み立てる値を同じ形に揃えるための一段で、round-trip の不動点になる。
 */
export function normalizeHealthQuery(query: HealthQuery): HealthQuery {
  return {
    freshness: dedupe(
      query.freshness
        .map(normalizeFreshnessStatus)
        .filter((v): v is FreshnessStatus => v !== null),
    ),
    alarm: dedupe(
      query.alarm
        .map(normalizeAlarmStatus)
        .filter((v): v is AlarmStatus => v !== null),
    ),
    healthStatus: dedupe(
      query.healthStatus
        .map(normalizeHealthStatus)
        .filter((v): v is HealthStatus => v !== null),
    ),
    olderThanSeconds: usableSeconds(query.olderThanSeconds),
    buildingDtId: trimToUndefined(query.buildingDtId),
    floorDtId: trimToUndefined(query.floorDtId),
    deviceDtId: trimToUndefined(query.deviceDtId),
    gatewayId: trimToUndefined(query.gatewayId),
    tags: normalizeTags(query.tags),
    q: trimToUndefined(query.q),
    sort: normalizeSort(query.sort),
    limit: clampLimit(query.limit),
    offset: clampOffset(query.offset),
  };
}

/**
 * 検索条件を URL クエリに書き出す。既定値のフィールドは出力しない（URL を短く保ち、
 * `parseHealthQuery` との round-trip を安定させる）。
 */
export function serializeHealthQuery(query: HealthQuery): URLSearchParams {
  const q = normalizeHealthQuery(query);
  const params = new URLSearchParams();

  if (q.freshness.length > 0) params.set("freshness", q.freshness.join(","));
  if (q.alarm.length > 0) params.set("alarm", q.alarm.join(","));
  if (q.healthStatus.length > 0)
    params.set("healthStatus", q.healthStatus.join(","));
  if (q.olderThanSeconds !== undefined)
    params.set("olderThan", String(q.olderThanSeconds));
  if (q.buildingDtId !== undefined) params.set("buildingDtId", q.buildingDtId);
  if (q.floorDtId !== undefined) params.set("floorDtId", q.floorDtId);
  if (q.deviceDtId !== undefined) params.set("deviceDtId", q.deviceDtId);
  if (q.gatewayId !== undefined) params.set("gatewayId", q.gatewayId);
  // tag はカンマを含みうるので繰り返しパラメータで出す（連結すると分解時に壊れる）。
  for (const tag of q.tags) params.append("tag", tag);
  if (q.q !== undefined) params.set("q", q.q);
  if (q.sort !== DEFAULT_HEALTH_QUERY.sort) params.set("sort", q.sort);
  if (q.limit !== DEFAULT_HEALTH_QUERY.limit)
    params.set("limit", String(q.limit));
  if (q.offset !== DEFAULT_HEALTH_QUERY.offset)
    params.set("offset", String(q.offset));

  return params;
}
