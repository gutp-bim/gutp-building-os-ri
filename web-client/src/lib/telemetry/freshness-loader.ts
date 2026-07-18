import {
  classifyPointFreshness,
  type PointFreshness,
  type PointLastSeen,
} from "./freshness";
import { resolveStaleThresholdSeconds } from "./freshness-threshold";
import { latestTelemetryBatch } from "./repository";

/** Fetches the latest-sample timestamps for many points at once. Injectable so the loader is testable offline. */
export type LatestBatchFetcher = (pointIds: string[]) => Promise<PointLastSeen[]>;

export type LoadPointsFreshnessOptions = {
  now: Date;
  /** System-default stale threshold (seconds); used for points with no expected interval. */
  thresholdSeconds: number;
  /**
   * Per-point expected telemetry interval (seconds), keyed by pointId (#183). When a point has one,
   * its stale threshold becomes `interval × intervalMultiplier`; otherwise `thresholdSeconds` applies.
   * Omit entirely to keep the previous single-threshold behaviour.
   */
  expectedIntervalSeconds?: Map<string, number | null | undefined>;
  /** Multiplier N for `expectedInterval → threshold`. Defaults to the registry default (3). */
  intervalMultiplier?: number;
  /** Batch latest-sample fetcher; defaults to the telemetry repository façade (#182). */
  fetchLatestBatch?: LatestBatchFetcher;
};

/**
 * Load and classify freshness for many points at once.
 *
 * A single batch request (`POST /telemetries/query/batch-latest`, #182) replaces the previous
 * per-point N+1 fan-out, so a floor with hundreds of points is one round-trip instead of hundreds of
 * concurrent browser requests. A point the batch *omits* (a non-admin cannot read it) is classified
 * `missing`, matching {@link classifyPointFreshness}'s "no sample ⇒ missing" rule.
 *
 * Each point's stale threshold is derived from its expected telemetry interval when known
 * (`interval × intervalMultiplier`, #183), falling back to `thresholdSeconds`; see
 * {@link resolveStaleThresholdSeconds}.
 *
 * A *failed* fetch, by contrast, is rethrown rather than masked as all-missing (#182 review): the
 * caller (operator home) must be able to tell "the data is unavailable" apart from "the points
 * genuinely have no data", so it can show an error instead of a fleet of false 欠測.
 */
export async function loadPointsFreshness(
  pointIds: string[],
  {
    now,
    thresholdSeconds,
    expectedIntervalSeconds,
    intervalMultiplier,
    fetchLatestBatch = (ids) => latestTelemetryBatch(ids),
  }: LoadPointsFreshnessOptions,
): Promise<PointFreshness[]> {
  if (pointIds.length === 0) return [];

  const rows = await fetchLatestBatch(pointIds);
  const seen = new Map(rows.map((r) => [r.pointId, r.lastSeen]));

  const lastSeen: PointLastSeen[] = pointIds.map((pointId) => ({
    pointId,
    lastSeen: seen.get(pointId) ?? null,
    thresholdSeconds: expectedIntervalSeconds
      ? resolveStaleThresholdSeconds({
          expected: { point: expectedIntervalSeconds.get(pointId) },
          multiplier: intervalMultiplier,
          systemDefaultThresholdSeconds: thresholdSeconds,
        })
      : undefined,
  }));
  return classifyPointFreshness(lastSeen, now, thresholdSeconds);
}
