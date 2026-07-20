import {
  type AlarmThresholds,
  classifyPointAlarms,
  type PointAlarm,
} from "./alarm";
import type { PointLastSeen } from "./freshness";
import { latestTelemetryBatch } from "./repository";

/** Batch latest-sample fetcher (carries the current value, #158 Phase 2a). Injectable for tests. */
export type LatestBatchFetcher = (pointIds: string[]) => Promise<PointLastSeen[]>;

/**
 * Load and classify value-threshold alarms for many points at once (#158 Phase 2a, ADR-0005).
 *
 * One `batch-latest` call returns each point's current value; each is classified against its opt-in
 * per-point thresholds (`thresholdsByPoint`). Points with no thresholds, or no usable value, come back
 * `unknown` and are never surfaced as alarms. Pure/derived-on-read: no state is retained between calls.
 *
 * A *failed* fetch is rethrown (like {@link loadPointsFreshness}) so the caller can show an error rather
 * than a false "all clear".
 */
export async function loadPointsAlarms(
  pointIds: string[],
  thresholdsByPoint: Map<string, AlarmThresholds | undefined>,
  fetchLatestBatch: LatestBatchFetcher = (ids) => latestTelemetryBatch(ids),
): Promise<PointAlarm[]> {
  if (pointIds.length === 0) return [];

  const rows = await fetchLatestBatch(pointIds);
  const valueById = new Map(rows.map((r) => [r.pointId, r.value ?? null]));

  return classifyPointAlarms(
    pointIds.map((pointId) => ({
      pointId,
      value: valueById.get(pointId) ?? null,
      thresholds: thresholdsByPoint.get(pointId),
    })),
  );
}
