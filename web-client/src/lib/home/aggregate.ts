import type { AlarmThresholds, PointAlarm } from "@/lib/telemetry/alarm";
import type { PointFreshness } from "@/lib/telemetry/freshness";

/**
 * A point id paired with its human-readable name and (optionally) the twin context an operator needs
 * to locate it — the owning device (equipment) and space (room). Populated by the floor traversal.
 */
export type NamedPoint = {
  pointId: string;
  name: string;
  deviceName?: string;
  spaceName?: string;
  /** Expected telemetry interval (seconds) for per-point stale detection (#183); undefined = unknown. */
  expectedIntervalSeconds?: number | null;
  /** Opt-in per-point alarm thresholds (#158 Phase 2a); undefined/empty = not evaluated. */
  thresholds?: AlarmThresholds;
};

/**
 * Attention status, worst-first: `critical`/`warn` are value-threshold alarms (#158 Phase 2a), `missing`
 * (never/unparseable) and `stale` (old) are freshness (#158 Phase 1). Value alarms outrank freshness —
 * a wrong value is more urgent than a late one.
 */
export type AttentionStatus = "critical" | "warn" | "missing" | "stale";

/** A point needing operator attention: an alarm (value) or a freshness (arrival) issue. */
export type AttentionItem = {
  pointId: string;
  name: string;
  status: AttentionStatus;
  /** Whole seconds since the last sample (freshness items); null/omitted for alarm items. */
  ageSeconds?: number | null;
  /** Current value (alarm items) and which bound it breached; omitted for freshness items. */
  value?: number | null;
  breach?: "high" | "low" | null;
  deviceName?: string;
  spaceName?: string;
};

const RANK: Record<AttentionStatus, number> = {
  critical: 0,
  warn: 1,
  missing: 2,
  stale: 3,
};

/**
 * Keep only alarms that are trustworthy *now*: a value from a point whose data is stale or missing can't
 * be read as a live threshold breach (#158 Phase 2a). Such a point surfaces as its freshness issue
 * instead, so the operator isn't shown 「異常値」 on an hour-old reading. Callers filter alarms through
 * this before counting/listing them.
 */
export function activeAlarms(
  alarms: PointAlarm[],
  freshness: PointFreshness[],
): PointAlarm[] {
  const notFresh = new Set(
    freshness
      .filter((f) => f.status === "stale" || f.status === "missing")
      .map((f) => f.pointId),
  );
  return alarms.filter((a) => !notFresh.has(a.pointId));
}

/**
 * Build the operator "needs attention" list from per-point **alarms** (value thresholds, #158 Phase 2a)
 * and **freshness** (arrival, #158 Phase 1). Alarms (critical/warn) and freshness issues (missing/stale)
 * are merged into one worst-first list; a point that is both keeps its worst status (a value alarm
 * outranks a freshness issue). Join twin context (name + device + space) and sort:
 * critical → warn → missing → stale, then within a status by severity (age desc for stale, name else).
 * Pure and deterministic (unit-tested). `alarms` defaults to empty so freshness-only callers are unchanged.
 */
export function buildAttentionList(
  named: NamedPoint[],
  freshness: PointFreshness[],
  alarms: PointAlarm[] = [],
): AttentionItem[] {
  const byId = new Map(named.map((n) => [n.pointId, n]));
  const ctx = (pointId: string) => {
    const meta = byId.get(pointId);
    return {
      name: meta?.name ?? pointId,
      deviceName: meta?.deviceName,
      spaceName: meta?.spaceName,
    };
  };

  // One entry per point, keeping the worst status. Alarms are considered first (lower rank), so a
  // point that is both critical and stale lands as critical.
  const worst = new Map<string, AttentionItem>();
  const consider = (item: AttentionItem) => {
    const prev = worst.get(item.pointId);
    if (!prev || RANK[item.status] < RANK[prev.status]) worst.set(item.pointId, item);
  };

  for (const a of alarms) {
    if (a.status !== "critical" && a.status !== "warn") continue;
    consider({
      pointId: a.pointId,
      status: a.status,
      value: a.value,
      breach: a.breach,
      ...ctx(a.pointId),
    });
  }

  for (const f of freshness) {
    if (f.status !== "stale" && f.status !== "missing") continue;
    consider({
      pointId: f.pointId,
      status: f.status,
      ageSeconds: f.ageSeconds,
      ...ctx(f.pointId),
    });
  }

  return [...worst.values()].sort((a, b) => {
    if (a.status !== b.status) return RANK[a.status] - RANK[b.status];
    // stale: oldest (largest age) first; nulls last. others: by name.
    if (a.status === "stale") return (b.ageSeconds ?? -1) - (a.ageSeconds ?? -1);
    return a.name.localeCompare(b.name);
  });
}
