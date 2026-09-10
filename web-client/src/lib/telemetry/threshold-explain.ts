/**
 * 鮮度・値異常の「判定根拠」を日本語 1 行に組み立てる純粋関数群（#457）。
 *
 * 鮮度バッジ（{@link ../../components/telemetry/freshness-badge}）は「鮮度切れかどうか」しか
 * 語らないため、運用者は *なぜ* 鮮度切れなのかを画面から読み取れなかった。判定モデル自体は
 * #183 で既にあり（`threshold = sbco:interval × telemetry.staleIntervalMultiplier`、無ければ
 * `telemetry.staleThresholdSeconds`）、Point 詳細はその入力値をすべて持っている。つまり新しい
 * API を叩かずに説明できる — ここはその文言生成だけを担う。
 *
 * 判定そのものは {@link ./freshness-threshold} / {@link ./alarm} が正本で、ここでは再計算しない
 * （`explainStaleThreshold` は同じ `resolveStaleThresholdSeconds` を呼ぶ）。表示専用・副作用なし。
 */

import type { AlarmThresholds, PointAlarm } from "./alarm";
import { DEFAULT_STALE_THRESHOLD_SECONDS } from "./freshness";
import {
  DEFAULT_STALE_INTERVAL_MULTIPLIER,
  resolveStaleThresholdSeconds,
} from "./freshness-threshold";

/** 有効な倍率 / 周期は「有限かつ正」であること（`freshness-threshold` と同じ判定）。 */
function isUsable(v: number | null | undefined): v is number {
  return typeof v === "number" && Number.isFinite(v) && v > 0;
}

/** 小数第 1 位までに丸めて、余分な `.0` を落とす（`1.0分` ではなく `1分`）。 */
function trimNumber(v: number): string {
  return String(Math.round(v * 10) / 10);
}

/**
 * 秒数を日本語の期間表記に丸める（秒 → 分 → 時間 → 日）。割り切れない場合は小数第 1 位まで
 * （90 秒 → `1.5分`）。負値・NaN・Infinity は `0秒` に倒す（表示が壊れるより無難）。
 */
export function formatDurationJa(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return "0秒";
  if (seconds < 60) return `${trimNumber(seconds)}秒`;
  if (seconds < 3600) return `${trimNumber(seconds / 60)}分`;
  if (seconds < 86400) return `${trimNumber(seconds / 3600)}時間`;
  return `${trimNumber(seconds / 86400)}日`;
}

export type StaleThresholdExplanation = {
  /** 実際に適用される鮮度閾値（秒）。{@link resolveStaleThresholdSeconds} と一致する。 */
  thresholdSeconds: number;
  /** 期待周期由来（`interval × N`）なら true、システム既定値なら false。 */
  usesExpectedInterval: boolean;
  /** 表示用の判定根拠 1 行。 */
  text: string;
};

/**
 * 鮮度閾値の根拠文を組み立てる。
 *
 * - 期待周期あり → `期待周期 5分 × 3 = 15分`
 * - 期待周期なし → `既定閾値 5分（期待周期が未設定のため倍率は適用されません）`
 *   （倍率を掛ける対象が無いので #183 のとおり倍率は適用されない。この但し書きが無いと
 *   「倍率が効いていない不具合」に見える。）
 */
export function explainStaleThreshold({
  expectedIntervalSeconds,
  staleThresholdSeconds = DEFAULT_STALE_THRESHOLD_SECONDS,
  staleIntervalMultiplier,
}: {
  expectedIntervalSeconds?: number | null;
  staleThresholdSeconds?: number;
  staleIntervalMultiplier?: number;
}): StaleThresholdExplanation {
  const thresholdSeconds = resolveStaleThresholdSeconds({
    expected: { point: expectedIntervalSeconds },
    multiplier: staleIntervalMultiplier,
    systemDefaultThresholdSeconds: staleThresholdSeconds,
  });

  if (!isUsable(expectedIntervalSeconds)) {
    return {
      thresholdSeconds,
      usesExpectedInterval: false,
      text: `既定閾値 ${formatDurationJa(thresholdSeconds)}（期待周期が未設定のため倍率は適用されません）`,
    };
  }

  const multiplier = isUsable(staleIntervalMultiplier)
    ? staleIntervalMultiplier
    : DEFAULT_STALE_INTERVAL_MULTIPLIER;
  return {
    thresholdSeconds,
    usesExpectedInterval: true,
    text: `期待周期 ${formatDurationJa(expectedIntervalSeconds)} × ${trimNumber(multiplier)} = ${formatDurationJa(thresholdSeconds)}`,
  };
}

/** 単位付きの数値表記（単位が無ければ数値のみ）。 */
function withUnit(value: number, unitLabel?: string | null): string {
  return unitLabel ? `${value} ${unitLabel}` : `${value}`;
}

/**
 * 値異常（#158 Phase 2a / `bos:alarmHigh` 等）の判定根拠 1 行。
 *
 * `critical` は外側（異常）閾値、`warn` は内側（注意）閾値を破ったことを意味するので、破られた
 * 側の閾値そのものを名指しする。閾値未設定・値なし（`unknown`）は判定対象外なので null を返し、
 * 呼び出し側は行ごと出さない — 「未評価」を「正常」と描くと安全側に見えて危険。
 */
export function explainAlarm(
  alarm: PointAlarm,
  thresholds: AlarmThresholds,
  unitLabel?: string | null,
): string | null {
  if (alarm.status === "unknown") return null;
  if (alarm.status === "ok") return "正常範囲内";

  const critical = alarm.status === "critical";
  const bound =
    alarm.breach === "high"
      ? critical
        ? thresholds.alarmHigh
        : thresholds.warnHigh
      : critical
        ? thresholds.alarmLow
        : thresholds.warnLow;
  if (!Number.isFinite(bound as number) || bound == null) return null;

  const name = `${critical ? "異常" : "注意"}${alarm.breach === "high" ? "上限" : "下限"}`;
  const verb = alarm.breach === "high" ? "超過" : "下回り";
  const current =
    alarm.value === null
      ? ""
      : `（現在値 ${withUnit(alarm.value, unitLabel)}）`;
  return `${name} ${withUnit(bound, unitLabel)} ${verb}${current}`;
}
