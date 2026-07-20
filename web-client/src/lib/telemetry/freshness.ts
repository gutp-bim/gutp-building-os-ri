/**
 * Pure point-freshness classification. Given each point's last telemetry timestamp and a threshold,
 * bucket points into fresh / stale / missing so an operator view can surface "何が届いていないか".
 *
 * The threshold is the backend-editable `telemetry.staleThresholdSeconds` setting (SettingsRegistry,
 * default 300s = 5 min, aligned with the default Parquet flush interval — see
 * `docs/oss-sla-freshness.md`). Callers fetch it via `lib/system-settings/fetch-settings.ts`, falling
 * back to {@link DEFAULT_STALE_THRESHOLD_SECONDS}. All I/O (listing points, fetching each point's
 * latest sample) stays in the caller/repository layer; this module is pure and takes an injected
 * `now`, so it is fully deterministic and unit-testable.
 */

/** Registry default for `telemetry.staleThresholdSeconds` (seconds). */
export const DEFAULT_STALE_THRESHOLD_SECONDS = 300;

export type FreshnessStatus = "fresh" | "stale" | "missing";

/** A point paired with the ISO-8601 timestamp of its most recent telemetry sample (null = none). */
export type PointLastSeen = {
  pointId: string;
  lastSeen: string | null;
  /** The most recent numeric value (#158 Phase 2a alarm evaluation); null when none/non-numeric. */
  value?: number | null;
  /**
   * Per-point stale threshold in seconds (#183). When set, it overrides the `thresholdSeconds`
   * argument for this point — this is how an expected-interval-derived threshold is applied to each
   * point individually. Omit to use the caller's shared default.
   */
  thresholdSeconds?: number;
};

export type PointFreshness = {
  pointId: string;
  status: FreshnessStatus;
  /** Whole seconds since the last sample; null when the point is missing / unparseable. */
  ageSeconds: number | null;
};

export type FreshnessSummary = {
  fresh: number;
  stale: number;
  missing: number;
  total: number;
};

/**
 * Classify each point's freshness relative to `now`:
 * - `missing` — no sample, or an unparseable timestamp,
 * - `stale`   — the last sample is strictly older than the point's threshold,
 * - `fresh`   — otherwise (the threshold boundary itself, and future timestamps from clock skew,
 *               count as fresh).
 *
 * `thresholdSeconds` is the shared default; a point may override it with its own
 * {@link PointLastSeen.thresholdSeconds} (the expected-interval-derived threshold, #183).
 *
 * Input order is preserved. The comparison uses milliseconds so the boundary is exact; the reported
 * `ageSeconds` is floored to whole seconds for display.
 */
export function classifyPointFreshness(
  points: PointLastSeen[],
  now: Date,
  thresholdSeconds: number,
): PointFreshness[] {
  const nowMs = now.getTime();

  return points.map(({ pointId, lastSeen, thresholdSeconds: perPoint }) => {
    if (lastSeen === null) {
      return { pointId, status: "missing", ageSeconds: null };
    }

    const lastSeenMs = new Date(lastSeen).getTime();
    if (Number.isNaN(lastSeenMs)) {
      return { pointId, status: "missing", ageSeconds: null };
    }

    const thresholdMs = (perPoint ?? thresholdSeconds) * 1000;
    const ageMs = nowMs - lastSeenMs;
    const status: FreshnessStatus = ageMs > thresholdMs ? "stale" : "fresh";
    // Future timestamps (ageMs < 0) floor toward 0 rather than showing a negative age.
    const ageSeconds = Math.max(0, Math.floor(ageMs / 1000));
    return { pointId, status, ageSeconds };
  });
}

/** Roll up per-point results into fresh/stale/missing counts plus the total. */
export function summarizeFreshness(
  results: PointFreshness[],
): FreshnessSummary {
  const summary: FreshnessSummary = {
    fresh: 0,
    stale: 0,
    missing: 0,
    total: results.length,
  };
  for (const r of results) {
    summary[r.status] += 1;
  }
  return summary;
}
