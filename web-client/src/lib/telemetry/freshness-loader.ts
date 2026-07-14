import {
  classifyPointFreshness,
  type PointFreshness,
  type PointLastSeen,
} from "./freshness";
import { latestTelemetry } from "./repository";
import type { TelemetryPoint } from "./types";

/** Fetches one point's most recent sample (or null). Injectable so the loader is testable offline. */
export type LatestSampleFetcher = (
  pointId: string,
) => Promise<TelemetryPoint | null>;

export type LoadPointsFreshnessOptions = {
  now: Date;
  thresholdSeconds: number;
  /** Per-point latest-sample fetcher; defaults to the telemetry repository façade. */
  fetchLatest?: LatestSampleFetcher;
};

/**
 * Load and classify freshness for many points at once.
 *
 * There is no batch "latest for many points" endpoint yet, so this fans out one latest-sample fetch
 * per point (`GET /telemetries/query?latest=true`, served from the Hot KV store). Centralising that
 * N+1 here means the future batch-endpoint optimisation touches only this file, and keeps the
 * per-point failure handling (a single point's fetch failing must not sink the whole view — it is
 * reported as `missing`) out of the pure {@link classifyPointFreshness}.
 *
 * The fetches run concurrently and are **not** bounded here — callers must scope `pointIds` to a
 * sensible batch (e.g. one floor/device at a time), not a whole building's point list, until a batch
 * endpoint or a concurrency limiter lands. A rejected per-point fetch is treated as `missing` rather
 * than propagated, so one dead point never fails the whole load.
 */
export async function loadPointsFreshness(
  pointIds: string[],
  {
    now,
    thresholdSeconds,
    fetchLatest = (id) => latestTelemetry(id),
  }: LoadPointsFreshnessOptions,
): Promise<PointFreshness[]> {
  const lastSeen: PointLastSeen[] = await Promise.all(
    pointIds.map(async (pointId) => {
      try {
        const latest = await fetchLatest(pointId);
        return { pointId, lastSeen: latest?.t ?? null };
      } catch {
        return { pointId, lastSeen: null };
      }
    }),
  );
  return classifyPointFreshness(lastSeen, now, thresholdSeconds);
}
