/**
 * データ健全性（`/health`, #452）のドメイン型。
 *
 * サーバ側の分類器（`BuildingOS.Shared.Domain.Health.PointHealthClassifier`）と 1:1 で対応する
 * union を 1 箇所に置く。3 軸（鮮度 / 値アラーム / ゲートウェイ接続）は互いに独立で、
 * `healthStatus` はその派生値であって軸を潰した代替ではない（ADR-0004 / ADR-0005 / ADR-0007）。
 *
 * API は enum を **PascalCase 文字列**（`"Stale"` / `"NeverReceived"`）で返すので、UI 側では
 * ここで定義する小文字 union に {@link normalizeEnum} で正規化してから扱う。未知値は
 * `"unknown"` に倒す — 新しい状態値がサーバに増えても画面が壊れないようにするため。
 */

/** 到着（鮮度）軸。判定不能（index が温まっていない）は `unknown`。 */
export type FreshnessStatus = "fresh" | "stale" | "missing" | "unknown";

/**
 * 値アラーム軸。`suppressed` は「鮮度切れ/欠測なので届いていない値で異常判定しない」抑止状態、
 * `unknown` は「閾値未設定 or 値なしで未評価」。どちらも "正常" ではないので描き分ける。
 */
export type AlarmStatus =
  "normal" | "warn" | "critical" | "suppressed" | "unknown";

/**
 * 3 軸から導かれる総合ステータス。宣言順が **重篤度順**（worst first）で、`sort=worst` は
 * この順序をそのまま severity として使う（サーバ側 `HealthStatus` の enum 順と一致）。
 */
export type HealthStatus =
  "critical" | "warn" | "missing" | "stale" | "unknown" | "fresh";

/** 鮮度閾値の由来（Point 個別 → 機器 → ゲートウェイ → システム既定 の解決順）。 */
export type ThresholdSource = "point" | "device" | "gateway" | "system";

/** 欠測の理由。ゲートウェイ切断の方が説明力が高いので「一度も受信なし」より優先される。 */
export type MissingReason = "neverReceived" | "gatewayDisconnected" | "unknown";

/** 破られたアラーム閾値。`alarm*` が外側（異常）、`warn*` が内側（注意）。 */
export type AlarmBound = "alarmHigh" | "alarmLow" | "warnHigh" | "warnLow";

/** 重篤度順（worst first）の {@link HealthStatus} 一覧。`sort=worst` の並び順の正本。 */
export const HEALTH_STATUS_ORDER: readonly HealthStatus[] = [
  "critical",
  "warn",
  "missing",
  "stale",
  "unknown",
  "fresh",
];

export const FRESHNESS_STATUSES: readonly FreshnessStatus[] = [
  "fresh",
  "stale",
  "missing",
  "unknown",
];

export const ALARM_STATUSES: readonly AlarmStatus[] = [
  "normal",
  "warn",
  "critical",
  "suppressed",
  "unknown",
];

export const THRESHOLD_SOURCES: readonly ThresholdSource[] = [
  "point",
  "device",
  "gateway",
  "system",
];

export const MISSING_REASONS: readonly MissingReason[] = [
  "neverReceived",
  "gatewayDisconnected",
  "unknown",
];

export const ALARM_BOUNDS: readonly AlarmBound[] = [
  "alarmHigh",
  "alarmLow",
  "warnHigh",
  "warnLow",
];

/**
 * 小文字化キーの辞書を 1 度だけ作り、「表記ゆれを含む文字列 → 正規表記」の純関数を返す。
 * API の PascalCase（`"NeverReceived"`）も URL に手書きされた大文字（`"Stale"`）も同じ入口で
 * 吸収する。文字列でない値・未知の値は `null`（「捨てる」のか「`unknown` に倒す」のかは
 * 呼び出し側が決める）。
 */
function normalizerFor<T extends string>(
  values: readonly T[],
): (raw: unknown) => T | null {
  const lookup = new Map(values.map((v) => [v.toLowerCase(), v]));
  return (raw) =>
    typeof raw === "string"
      ? (lookup.get(raw.trim().toLowerCase()) ?? null)
      : null;
}

export const normalizeFreshnessStatus = normalizerFor(FRESHNESS_STATUSES);
export const normalizeAlarmStatus = normalizerFor(ALARM_STATUSES);
export const normalizeHealthStatus = normalizerFor(HEALTH_STATUS_ORDER);
export const normalizeThresholdSource = normalizerFor(THRESHOLD_SOURCES);
export const normalizeMissingReason = normalizerFor(MISSING_REASONS);
export const normalizeAlarmBound = normalizerFor(ALARM_BOUNDS);
