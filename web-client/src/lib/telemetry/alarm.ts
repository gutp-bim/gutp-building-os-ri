/**
 * Pure value-threshold alarm classification (#158 Phase 2a, ADR-0005).
 *
 * Given a point's current numeric value and its (opt-in) per-point thresholds, bucket it into
 * `ok` / `warn` / `critical` / `unknown` so the operator view can surface "何が異常値か" — a distinct
 * axis from freshness (data arrival) and gateway connection (#230).
 *
 * **Stateless / derived-on-read** (ADR-0005 D2 Phase 2a): this is a snapshot classifier with no memory
 * of prior state, so there is no flapping to damp and no hysteresis. Thresholds are **opt-in per point**
 * (from the twin, `bos:alarmHigh`/`alarmLow` [+ `warnHigh`/`warnLow`]); a point with no thresholds, or
 * with no current value, is `unknown` and simply not counted as an alarm.
 *
 * All I/O (reading the latest value, the twin thresholds) stays in the caller/repository layer; this
 * module is pure and deterministic (unit-tested).
 */

export type AlarmStatus = "ok" | "warn" | "critical" | "unknown";

/** Which bound a value breached, for display; null when `ok`/`unknown`. */
export type AlarmBreach = "high" | "low" | null;

/**
 * Opt-in per-point alarm thresholds. Each bound is independent and optional: set only `alarmHigh` for a
 * high-only alarm, both for a band, add `warnHigh`/`warnLow` for a two-stage (warn→critical) alarm.
 * `critical` bounds are the outer limits; `warn` bounds are the inner (earlier) ones.
 */
export type AlarmThresholds = {
  alarmHigh?: number | null;
  alarmLow?: number | null;
  warnHigh?: number | null;
  warnLow?: number | null;
};

/** A point's current value paired with its thresholds (thresholds omitted/empty ⇒ not evaluated). */
export type PointValue = {
  pointId: string;
  value: number | null;
  thresholds?: AlarmThresholds;
};

export type PointAlarm = {
  pointId: string;
  status: AlarmStatus;
  value: number | null;
  breach: AlarmBreach;
};

function hasAnyThreshold(t: AlarmThresholds | undefined): t is AlarmThresholds {
  if (!t) return false;
  return (
    isNum(t.alarmHigh) ||
    isNum(t.alarmLow) ||
    isNum(t.warnHigh) ||
    isNum(t.warnLow)
  );
}

function isNum(v: number | null | undefined): v is number {
  return typeof v === "number" && !Number.isNaN(v);
}

/**
 * Classify one point's value against its thresholds. `critical` (outer limits) wins over `warn` (inner),
 * and high/low are checked independently. Returns `unknown` when the point has no thresholds configured
 * or no usable current value — those are never surfaced as alarms.
 */
export function classifyPointAlarm(point: PointValue): PointAlarm {
  const { pointId, value } = point;
  if (!hasAnyThreshold(point.thresholds) || !isNum(value)) {
    return { pointId, status: "unknown", value, breach: null };
  }
  const { alarmHigh, alarmLow, warnHigh, warnLow } = point.thresholds;

  // Critical (outer limits) first — a value past the critical bound is critical even if it also
  // passed the warn bound.
  if (isNum(alarmHigh) && value >= alarmHigh)
    return { pointId, status: "critical", value, breach: "high" };
  if (isNum(alarmLow) && value <= alarmLow)
    return { pointId, status: "critical", value, breach: "low" };
  if (isNum(warnHigh) && value >= warnHigh)
    return { pointId, status: "warn", value, breach: "high" };
  if (isNum(warnLow) && value <= warnLow)
    return { pointId, status: "warn", value, breach: "low" };
  return { pointId, status: "ok", value, breach: null };
}

export function classifyPointAlarms(points: PointValue[]): PointAlarm[] {
  return points.map(classifyPointAlarm);
}

export type AlarmSummary = { critical: number; warn: number };

/** Count only the actionable alarm states (ok/unknown are not surfaced). */
export function summarizeAlarms(results: PointAlarm[]): AlarmSummary {
  const summary: AlarmSummary = { critical: 0, warn: 0 };
  for (const r of results) {
    if (r.status === "critical") summary.critical += 1;
    else if (r.status === "warn") summary.warn += 1;
  }
  return summary;
}
