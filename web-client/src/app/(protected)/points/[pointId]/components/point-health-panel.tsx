/**
 * Point 詳細の「データ健全性」パネル（#457）。
 *
 * 鮮度バッジ（{@link FreshnessBadge}）だけでは *なぜ* 鮮度切れなのかが画面から読み取れない。
 * 判定モデル自体は #183 で決まっており（`threshold = sbco:interval × staleIntervalMultiplier`、
 * 期待周期が無ければ既定閾値）、その入力値は Point 詳細がすでに全部持っている — つまり
 * **新しい API を叩かずに** 判定根拠を出せる。ここはその表示だけを担う。
 *
 * 判定そのものは `lib/telemetry/` が正本（鮮度 = `classifyPointFreshness`、閾値 =
 * `explainStaleThreshold`、値異常 = `classifyPointAlarm` / `explainAlarm`）で、ここでは再実装
 * しない。`now` は注入させて描画を決定的に保つ。
 *
 * 24h 受信状況バーは #457 の follow-up。
 */

import { FreshnessBadge } from "@/components/telemetry/freshness-badge";
import {
  classifyPointAlarm,
  type AlarmThresholds,
} from "@/lib/telemetry/alarm";
import {
  classifyPointFreshness,
  DEFAULT_STALE_THRESHOLD_SECONDS,
} from "@/lib/telemetry/freshness";
import { formatAge } from "@/lib/telemetry/freshness-format";
import {
  explainAlarm,
  explainStaleThreshold,
  formatDurationJa,
} from "@/lib/telemetry/threshold-explain";
import type { TelemetryLatestSample } from "@/lib/telemetry/types";
import { formatResolvedValue } from "@/lib/telemetry/value";
import { resolveUnitLabel } from "@/lib/utils/helper/telemetry-helper";

/**
 * scale を掛けた後の浮動小数点誤差を落とす（234 × 0.1 = 23.400000000000002 → 23.4）。
 * 有効桁 12 桁で丸めるのは、実測値の桁数を削らずに二進丸め誤差だけを吸収できるため。
 */
function applyScale(value: number, scale: number): number {
  const scaled = value * scale;
  return Number.isFinite(scaled) ? Number(scaled.toPrecision(12)) : scaled;
}

/** 最新値の表示文字列。数値のみ scale と単位を適用し、文字列 / 真偽値はそのまま文言化する。 */
function formatLatestValue(
  latest: TelemetryLatestSample | null,
  scale: number,
  unitLabel: string | null,
): string {
  const resolved = latest?.value;
  if (!resolved || resolved.kind === "none") return "-";
  if (resolved.kind !== "number") return formatResolvedValue(resolved) ?? "-";

  const value = applyScale(resolved.value, scale);
  return unitLabel ? `${value} ${unitLabel}` : `${value}`;
}

/** ラベル + 値の 1 行。値側にだけ testid を付けて、文言の同居でテストが壊れないようにする。 */
function Row({
  label,
  testId,
  children,
}: {
  label: string;
  testId: string;
  children: React.ReactNode;
}) {
  return (
    <>
      <dt className="font-semibold text-gray-700">{label}</dt>
      <dd data-testid={testId}>{children}</dd>
    </>
  );
}

export function PointHealthPanel({
  latest,
  now,
  scale = 1,
  unit,
  expectedIntervalSeconds,
  staleThresholdSeconds = DEFAULT_STALE_THRESHOLD_SECONDS,
  staleIntervalMultiplier,
  alarmThresholds,
  deviceName,
}: {
  /** 最新の受信サンプル。null = 一度も受信していない（欠測）。 */
  latest: TelemetryLatestSample | null;
  /** 判定の基準時刻（注入して描画を決定的にする）。 */
  now: Date;
  scale?: number;
  /** ポイントの単位（QUDT IRI / 短縮コードのどちらでも可）。 */
  unit?: string | null;
  /** 期待更新周期（秒, sbco:interval）。null / 未設定なら既定閾値に倒れる。 */
  expectedIntervalSeconds?: number | null;
  /** システム既定の鮮度閾値と倍率（GET /api/telemetry/config, #183）。 */
  staleThresholdSeconds?: number;
  staleIntervalMultiplier?: number;
  /** 値異常閾値（#158 Phase 2a）。未設定なら値異常行そのものを出さない。 */
  alarmThresholds?: AlarmThresholds;
  /** 所属機器名。不明なら行ごと出さない。 */
  deviceName?: string | null;
}) {
  const unitLabel = resolveUnitLabel(unit);

  // 鮮度閾値と、その根拠 1 行。閾値の解決は explainStaleThreshold が
  // resolveStaleThresholdSeconds に委譲しているので、ここで再計算はしない。
  const explanation = explainStaleThreshold({
    expectedIntervalSeconds,
    staleThresholdSeconds,
    staleIntervalMultiplier,
  });
  const freshness = classifyPointFreshness(
    [{ pointId: "", lastSeen: latest?.t ?? null }],
    now,
    explanation.thresholdSeconds,
  )[0];

  const lastSeenText =
    latest?.t && freshness.ageSeconds !== null
      ? `${new Date(latest.t).toLocaleTimeString("ja-JP")}（${formatAge(freshness.ageSeconds)}）`
      : "受信なし";

  // 値異常は数値の最新値にしか効かない。閾値・値のどちらかが無ければ classifyPointAlarm は
  // unknown を返し、explainAlarm は null を返す → 行ごと出さない（未評価を「正常」と描かない）。
  const numericValue =
    latest?.value.kind === "number"
      ? applyScale(latest.value.value, scale)
      : null;
  const alarmText = explainAlarm(
    classifyPointAlarm({
      pointId: "",
      value: numericValue,
      thresholds: alarmThresholds,
    }),
    alarmThresholds ?? {},
    unitLabel,
  );

  return (
    <div
      data-testid="point-health-panel"
      className="bg-white p-4 rounded-lg shadow"
    >
      <div className="flex items-center justify-between mb-3">
        <h3 className="font-semibold">データ健全性</h3>
        <FreshnessBadge freshness={freshness} />
      </div>
      <dl className="grid grid-cols-[8rem_1fr] gap-y-2 text-sm">
        <Row label="最新値" testId="health-latest-value">
          {formatLatestValue(latest, scale, unitLabel)}
        </Row>
        <Row label="最終受信" testId="health-last-seen">
          {lastSeenText}
        </Row>
        <Row label="期待更新周期" testId="health-expected-interval">
          {explanation.usesExpectedInterval && expectedIntervalSeconds
            ? formatDurationJa(expectedIntervalSeconds)
            : "未設定"}
        </Row>
        <Row label="鮮度判定" testId="health-threshold">
          {/* 「超過」は実際に鮮度切れのときだけ添える。閾値の提示自体は常に出す。 */}
          {freshness.status === "stale"
            ? `${explanation.text} を超過`
            : explanation.text}
        </Row>
        {alarmText && (
          <Row label="値異常" testId="health-alarm">
            {alarmText}
          </Row>
        )}
        {deviceName && (
          <Row label="機器" testId="health-device">
            {deviceName}
          </Row>
        )}
      </dl>
    </div>
  );
}
