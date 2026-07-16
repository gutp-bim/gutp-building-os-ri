import {
  classifyPointFreshness,
  type PointFreshness,
  type PointLastSeen,
} from "./freshness";
import { latestTelemetryBatch } from "./repository";

/** Fetches the latest-sample timestamps for many points at once. Injectable so the loader is testable offline. */
export type LatestBatchFetcher = (pointIds: string[]) => Promise<PointLastSeen[]>;

export type LoadPointsFreshnessOptions = {
  now: Date;
  thresholdSeconds: number;
  /** Batch latest-sample fetcher; defaults to the telemetry repository façade (#182). */
  fetchLatestBatch?: LatestBatchFetcher;
};

/**
 * Load and classify freshness for many points at once.
 *
 * A single batch request (`POST /telemetries/query/batch-latest`, #182) replaces the previous
 * per-point N+1 fan-out, so a floor with hundreds of points is one round-trip instead of hundreds of
 * concurrent browser requests. Every requested point is represented in the result: a point the batch
 * omits (a non-admin cannot read it), or a failed batch, is treated as `missing` rather than dropped,
 * so one dead point / a transient error never sinks the whole view — matching {@link classifyPointFreshness}'s
 * "no sample ⇒ missing" rule.
 */
export async function loadPointsFreshness(
  pointIds: string[],
  {
    now,
    thresholdSeconds,
    fetchLatestBatch = (ids) => latestTelemetryBatch(ids),
  }: LoadPointsFreshnessOptions,
): Promise<PointFreshness[]> {
  if (pointIds.length === 0) return [];

  let seen: Map<string, string | null>;
  try {
    const rows = await fetchLatestBatch(pointIds);
    seen = new Map(rows.map((r) => [r.pointId, r.lastSeen]));
  } catch {
    // A failed batch degrades to "everything missing" rather than throwing the whole view.
    seen = new Map();
  }

  const lastSeen: PointLastSeen[] = pointIds.map((pointId) => ({
    pointId,
    lastSeen: seen.get(pointId) ?? null,
  }));
  return classifyPointFreshness(lastSeen, now, thresholdSeconds);
}
