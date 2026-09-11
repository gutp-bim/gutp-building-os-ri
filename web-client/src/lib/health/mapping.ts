/**
 * データ健全性 API（`/health`, #452）の wire 形とドメイン形の変換、および表示用ラベル。
 *
 * ここは **純粋・防御的**であることが要件。判定そのものはサーバ側の分類器
 * （`PointHealthClassifier`）が正本で、フロントは再判定しない（ADR-0007: 判定ロジックの正本は 1 つ）。
 * したがってこのモジュールがするのは「読めない・欠けているフィールドを安全な既定値に倒す」ことと
 * 「日本語ラベルを与えること」だけ。
 *
 * - API の enum は **PascalCase 文字列**で来るので `types.ts` の小文字 union に正規化する。
 *   未知の状態値は `unknown` に倒す — サーバに新しい状態値が増えても一覧が落ちないようにする。
 * - 判定式の説明文は `lib/telemetry/threshold-explain.ts`（#457）を再利用する。同じ式を 2 箇所で
 *   文章化すると、片方だけ直したときに画面ごとに違う説明が出る。
 */

import { DEFAULT_STALE_THRESHOLD_SECONDS } from "@/lib/telemetry/freshness";
import { formatAge as formatAgeSeconds } from "@/lib/telemetry/freshness-format";
import { explainStaleThreshold } from "@/lib/telemetry/threshold-explain";
import {
  normalizeAlarmBound,
  normalizeAlarmStatus,
  normalizeFreshnessStatus,
  normalizeHealthStatus,
  normalizeMissingReason,
  normalizeThresholdSource,
  type AlarmBound,
  type AlarmStatus,
  type FreshnessStatus,
  type HealthStatus,
  type MissingReason,
  type ThresholdSource,
} from "./types";

/** `/health` 一覧の 1 行。3 軸（鮮度 / 値アラーム / ゲートウェイ接続）を潰さずに保持する。 */
export type HealthRow = {
  pointId: string;
  name: string;
  unit?: string;
  freshnessStatus: FreshnessStatus;
  alarmStatus: AlarmStatus;
  healthStatus: HealthStatus;
  /** 最終受信の ISO-8601 時刻。未受信・読めない値は null。 */
  lastSeen: string | null;
  /** 最終受信からの経過秒数（整数・0 以上）。判定不能・未受信は null。 */
  ageSeconds: number | null;
  expectedIntervalSeconds: number | null;
  thresholdSeconds: number;
  thresholdSource: ThresholdSource;
  /** 欠測の理由。欠測でない行は null。 */
  missingReason: MissingReason | null;
  value: number | null;
  violated: AlarmBound | null;
  gatewayId: string | null;
  /** ゲートウェイ接続状態。**不明（null）と切断（false）を混同しない**。 */
  gatewayConnected: boolean | null;
  deviceName?: string;
  spaceName?: string;
  floorName?: string;
  buildingName?: string;
  tags: string[];
};

/** 表示上の「値なし」。 */
const DASH = "—";

function asRecord(wire: unknown): Record<string, unknown> {
  return typeof wire === "object" && wire !== null && !Array.isArray(wire)
    ? (wire as Record<string, unknown>)
    : {};
}

/** 空白のみ・非文字列は「無し」（undefined）として扱う。 */
function optionalText(raw: unknown): string | undefined {
  if (typeof raw !== "string") return undefined;
  const trimmed = raw.trim();
  return trimmed === "" ? undefined : trimmed;
}

function nullableText(raw: unknown): string | null {
  return optionalText(raw) ?? null;
}

/** 有限な数値のみ採用する（`"23.4"` のような文字列は数値ではないので採らない）。 */
function finiteNumber(raw: unknown): number | null {
  return typeof raw === "number" && Number.isFinite(raw) ? raw : null;
}

/** 有限かつ正の数値のみ採用する（0 秒周期・0 秒閾値は「常に欠測」になるので採らない）。 */
function positiveNumber(raw: unknown): number | null {
  const value = finiteNumber(raw);
  return value !== null && value > 0 ? value : null;
}

function ageSecondsOf(raw: unknown): number | null {
  const value = finiteNumber(raw);
  // クロックずれ由来の負値は 0 にクリップする（サーバ側の分類器と同じ扱い）。
  return value === null ? null : Math.max(0, Math.floor(value));
}

function tagsOf(raw: unknown): string[] {
  if (!Array.isArray(raw)) return [];
  const tags = raw
    .map(optionalText)
    .filter((t): t is string => t !== undefined);
  return [...new Set(tags)];
}

/**
 * wire の 1 行をドメイン形に写す。**壊れた行でも throw しない** — 一覧の 1 行が壊れているだけで
 * 画面全体が出せなくなるのは運用上いちばん困る（データ健全性を見に来る画面である）。
 * `pointId` が読めない行は `""` になるので、呼び出し側（repository）が落とすこと。
 */
export function toHealthRow(wire: unknown): HealthRow {
  const w = asRecord(wire);

  const pointId = nullableText(w.pointId) ?? "";
  const freshnessStatus =
    normalizeFreshnessStatus(w.freshnessStatus) ?? "unknown";
  // 欠測でない行に理由が付いていると誤読されるので、欠測のときだけ採る。欠測なのに理由が
  // 読めなければ「原因不明」として残す（理由が消えるより読めた方が調査の役に立つ）。
  const missingReason =
    freshnessStatus === "missing"
      ? (normalizeMissingReason(w.missingReason) ??
        (w.missingReason == null ? null : "unknown"))
      : null;

  return {
    pointId,
    name: optionalText(w.name) ?? pointId,
    unit: optionalText(w.unit),
    freshnessStatus,
    alarmStatus: normalizeAlarmStatus(w.alarmStatus) ?? "unknown",
    healthStatus: normalizeHealthStatus(w.healthStatus) ?? "unknown",
    lastSeen: nullableText(w.lastSeen),
    ageSeconds: ageSecondsOf(w.ageSeconds),
    expectedIntervalSeconds: positiveNumber(w.expectedIntervalSeconds),
    thresholdSeconds:
      positiveNumber(w.thresholdSeconds) ?? DEFAULT_STALE_THRESHOLD_SECONDS,
    thresholdSource: normalizeThresholdSource(w.thresholdSource) ?? "system",
    missingReason,
    value: finiteNumber(w.value),
    violated: normalizeAlarmBound(w.violated),
    gatewayId: nullableText(w.gatewayId),
    gatewayConnected:
      typeof w.gatewayConnected === "boolean" ? w.gatewayConnected : null,
    deviceName: optionalText(w.deviceName),
    spaceName: optionalText(w.spaceName),
    floorName: optionalText(w.floorName),
    buildingName: optionalText(w.buildingName),
    tags: tagsOf(w.tags),
  };
}

/**
 * 経過秒数の日本語表記（`18分前`）。未受信・判定不能（null）は {@link DASH}。
 * 表記そのものは `lib/telemetry/freshness-format` が正本で、ここは null の扱いを足すだけ。
 */
export function formatAge(ageSeconds: number | null): string {
  return ageSeconds === null ? DASH : formatAgeSeconds(ageSeconds);
}

export function freshnessLabel(status: FreshnessStatus): string {
  switch (status) {
    case "fresh":
      return "最新";
    case "stale":
      return "鮮度切れ";
    case "missing":
      return "欠測";
    case "unknown":
      return "判定不能";
  }
}

/**
 * 値アラームのラベル。`suppressed`（届いていない値なので評価しない）と `unknown`（閾値未設定）は
 * **「正常」と描かない** — 未評価を正常に見せると安全側に見えて危険（#457 と同じ理由）。
 */
export function alarmLabel(status: AlarmStatus): string {
  switch (status) {
    case "normal":
      return "正常";
    case "warn":
      return "注意";
    case "critical":
      return "異常";
    case "suppressed":
      return "評価対象外";
    case "unknown":
      return "未評価";
  }
}

export function healthStatusLabel(status: HealthStatus): string {
  switch (status) {
    case "critical":
      return "異常";
    case "warn":
      return "注意";
    case "missing":
      return "欠測";
    case "stale":
      return "鮮度切れ";
    case "unknown":
      return "判定不能";
    case "fresh":
      return "正常";
  }
}

export function missingReasonLabel(reason: MissingReason | null): string {
  switch (reason) {
    case "neverReceived":
      return "受信履歴なし";
    case "gatewayDisconnected":
      return "ゲートウェイ切断";
    case "unknown":
      return "原因不明";
    case null:
      return DASH;
  }
}

export function thresholdSourceLabel(source: ThresholdSource): string {
  switch (source) {
    case "point":
      return "Point 個別";
    case "device":
      return "機器";
    case "gateway":
      return "ゲートウェイ";
    case "system":
      return "システム既定";
  }
}

export function alarmBoundLabel(bound: AlarmBound | null): string {
  switch (bound) {
    case "alarmHigh":
      return "異常上限";
    case "alarmLow":
      return "異常下限";
    case "warnHigh":
      return "注意上限";
    case "warnLow":
      return "注意下限";
    case null:
      return DASH;
  }
}

/**
 * 鮮度閾値の判定根拠 1 行（`期待周期 5分 × 3 = 15分`）。
 *
 * 文言は #457 の {@link explainStaleThreshold} をそのまま使う。行は解決済みの閾値しか持たない
 * ので、倍率は `閾値 ÷ 期待周期` として復元して渡す — こうすると必ず行の `thresholdSeconds` と
 * 一致した式になり、サーバ側の解決結果を UI が言い換えてしまうことがない。
 */
export function explainRowThreshold(row: HealthRow): string {
  const expected = row.expectedIntervalSeconds;
  return explainStaleThreshold({
    expectedIntervalSeconds: expected,
    staleThresholdSeconds: row.thresholdSeconds,
    staleIntervalMultiplier:
      expected === null ? undefined : row.thresholdSeconds / expected,
  }).text;
}
