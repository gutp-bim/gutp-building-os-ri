/**
 * Pure resolution of a point's stale threshold from its **expected telemetry interval** (#183).
 *
 * Equipment data has wildly different natural cadences (室温 1 分 / 電力量 30 分 / 設備状態 5 秒 /
 * 保守点検値 1 日), so a single fixed 300s stale threshold either false-alarms fast points or misses
 * slow ones. Instead we derive the threshold from each point's expected interval:
 *
 *     threshold = expectedInterval × N            (N = {@link DEFAULT_STALE_INTERVAL_MULTIPLIER})
 *
 * The expected interval itself is resolved through a hierarchy, most specific first:
 *
 *     point-specific  (Twin / Point metadata, sbco:interval)
 *       → device default
 *       → gateway default
 *       → (none) ⇒ fall back to the system-default stale threshold (the historical 300s registry
 *                  value from `telemetry.staleThresholdSeconds`) — the multiplier does NOT apply here
 *                  because there is no interval to multiply, preserving today's behaviour for points
 *                  that have no expected interval configured.
 *
 * Only the point tier is currently populated from the twin; `device` / `gateway` are accepted so the
 * hierarchy can be wired later (their twin defaults do not exist yet) without an API change.
 *
 * Both functions are pure and fully unit-testable.
 */

/** Registry default for `telemetry.staleIntervalMultiplier` (N in `threshold = interval × N`). */
export const DEFAULT_STALE_INTERVAL_MULTIPLIER = 3;

/**
 * The tiers of the expected-interval fallback, in priority order. Each is the point's expected
 * telemetry interval **in seconds** at that scope, or null/undefined when unset at that scope.
 */
export type ExpectedIntervalSources = {
  point?: number | null;
  device?: number | null;
  gateway?: number | null;
};

/** A usable expected interval is a finite, strictly-positive number of seconds. */
function isUsableInterval(v: number | null | undefined): v is number {
  return typeof v === "number" && Number.isFinite(v) && v > 0;
}

/**
 * Resolve the effective expected interval (seconds) by walking the point → device → gateway
 * hierarchy and returning the first tier that supplies a usable (finite, positive) value. Returns
 * null when no tier does, so the caller can fall back to the system-default threshold.
 */
export function resolveExpectedIntervalSeconds(
  sources: ExpectedIntervalSources,
): number | null {
  for (const tier of [sources.point, sources.device, sources.gateway]) {
    if (isUsableInterval(tier)) return tier;
  }
  return null;
}

/**
 * Resolve a point's stale threshold (seconds). When an expected interval is known (any tier),
 * `interval × multiplier`; otherwise the caller's system-default threshold. A non-positive or
 * non-finite multiplier reverts to {@link DEFAULT_STALE_INTERVAL_MULTIPLIER}.
 */
export function resolveStaleThresholdSeconds({
  expected,
  multiplier = DEFAULT_STALE_INTERVAL_MULTIPLIER,
  systemDefaultThresholdSeconds,
}: {
  expected: ExpectedIntervalSources;
  multiplier?: number;
  systemDefaultThresholdSeconds: number;
}): number {
  const interval = resolveExpectedIntervalSeconds(expected);
  if (interval === null) return systemDefaultThresholdSeconds;

  const n = isUsableInterval(multiplier)
    ? multiplier
    : DEFAULT_STALE_INTERVAL_MULTIPLIER;
  return interval * n;
}
