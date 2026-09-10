/**
 * データ品質一覧のバッジ群（#453）。`components/telemetry/freshness-badge.tsx` の形を踏襲した
 * 小さな pill で、**軸ごとに別のバッジ**を用意しているのが要点。
 *
 * 鮮度（データが届いているか）と値異常（届いた値が閾値内か）は独立した軸で、「最新かつ値異常」は
 * 普通に成立する。1 つのバッジに畳むとその状態を表現できなくなるので、混ぜない（ADR-0007）。
 */

import {
  alarmBoundLabel,
  alarmLabel,
  freshnessLabel,
  healthStatusLabel,
} from "@/lib/health/mapping";
import type {
  AlarmBound,
  AlarmStatus,
  FreshnessStatus,
  HealthStatus,
} from "@/lib/health/types";

const PILL =
  "inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium";

// AA コントラストの塗り。欠測は「無データ」であってアラームではないので中立のグレー。
const FRESHNESS_STYLES: Record<FreshnessStatus, string> = {
  fresh: "bg-green-100 text-green-800",
  stale: "bg-amber-100 text-amber-800",
  missing: "bg-gray-200 text-gray-700",
  unknown: "bg-gray-100 text-gray-600",
};

// 「評価対象外」「未評価」は正常ではないが警告でもないので、緑にも赤にもしない。
const ALARM_STYLES: Record<AlarmStatus, string> = {
  normal: "bg-green-100 text-green-800",
  warn: "bg-amber-100 text-amber-800",
  critical: "bg-red-100 text-red-800",
  suppressed: "bg-gray-100 text-gray-600",
  unknown: "bg-gray-100 text-gray-600",
};

const HEALTH_STYLES: Record<HealthStatus, string> = {
  critical: "bg-red-100 text-red-800",
  warn: "bg-amber-100 text-amber-800",
  missing: "bg-gray-200 text-gray-700",
  stale: "bg-amber-100 text-amber-800",
  unknown: "bg-gray-100 text-gray-600",
  fresh: "bg-green-100 text-green-800",
};

/** 鮮度（到着軸）のバッジ。 */
export function HealthFreshnessBadge({ status }: { status: FreshnessStatus }) {
  return (
    <span
      data-testid={`health-freshness-${status}`}
      className={`${PILL} ${FRESHNESS_STYLES[status]}`}
    >
      {freshnessLabel(status)}
    </span>
  );
}

/**
 * 値アラーム（値軸）のバッジ。破られた閾値が分かっていれば括弧で添える
 * （「異常」だけでは上限を超えたのか下限を割ったのか分からない）。
 */
export function HealthAlarmBadge({
  status,
  violated,
}: {
  status: AlarmStatus;
  violated?: AlarmBound | null;
}) {
  const bound = violated ? `（${alarmBoundLabel(violated)}）` : "";
  return (
    <span
      data-testid={`health-alarm-${status}`}
      className={`${PILL} ${ALARM_STYLES[status]}`}
    >
      {alarmLabel(status)}
      {bound}
    </span>
  );
}

/** 3 軸から導かれる総合ステータスのバッジ。 */
export function HealthStatusBadge({ status }: { status: HealthStatus }) {
  return (
    <span
      data-testid={`health-status-${status}`}
      className={`${PILL} ${HEALTH_STYLES[status]}`}
    >
      {healthStatusLabel(status)}
    </span>
  );
}

/**
 * Gateway の接続状態。**不明（null）と切断（false）を混同しない** — 接続状態を知らないことを
 * 「切断」と描くと、実際は動いている系統を落ちていると誤読させる。
 */
export function GatewayConnectionBadge({
  connected,
}: {
  connected: boolean | null;
}) {
  if (connected === null) {
    return (
      <span
        data-testid="health-gateway-unknown"
        className={`${PILL} bg-gray-100 text-gray-600`}
        title="接続状態は不明です"
      >
        不明
      </span>
    );
  }
  return connected ? (
    <span
      data-testid="health-gateway-connected"
      className={`${PILL} bg-green-100 text-green-800`}
    >
      接続中
    </span>
  ) : (
    <span
      data-testid="health-gateway-disconnected"
      className={`${PILL} bg-red-100 text-red-800`}
    >
      未接続
    </span>
  );
}
