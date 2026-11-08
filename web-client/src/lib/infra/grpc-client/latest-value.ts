/**
 * Pure helpers for the latest-value push stream (#636): turning a stream event into the same
 * {@link TelemetryLatestSample} the REST read produces, and the reconnect policy. Kept apart from the hook so
 * they are tested without a transport.
 */

import type { TelemetryLatestSample } from "@/lib/telemetry/types";

/** The fields of a `LatestValueEvent` this module reads (structural, so tests need no generated message). */
export type LatestValueEventLike = {
  pointId: string;
  value:
    | { case: "numberValue"; value: number }
    | { case: "stringValue"; value: string }
    | { case: "boolValue"; value: boolean }
    | { case: undefined; value?: undefined };
  datetime: string;
  snapshot: boolean;
};

/** A stream event as the point page's latest sample. An unset value is `{ kind: "none" }`, as from REST. */
export function toLatestSample(e: LatestValueEventLike): TelemetryLatestSample {
  const t = e.datetime.trim() === "" ? null : e.datetime;
  switch (e.value.case) {
    case "numberValue":
      return Number.isFinite(e.value.value)
        ? { t, value: { kind: "number", value: e.value.value } }
        : { t, value: { kind: "none" } };
    case "stringValue":
      return { t, value: { kind: "string", value: e.value.value } };
    case "boolValue":
      return { t, value: { kind: "boolean", value: e.value.value } };
    default:
      return { t, value: { kind: "none" } };
  }
}

function sameValue(
  a: TelemetryLatestSample["value"],
  b: TelemetryLatestSample["value"],
): boolean {
  if (a.kind !== b.kind) return false;
  return a.kind === "none" || ("value" in b && a.value === b.value);
}

/**
 * Whether a sample (pushed, or read over REST) should replace the one on screen. Older readings never win: the
 * page may have just refreshed over REST, and a snapshot from a reconnect can trail it. A **different value with
 * the same timestamp does** win — the hot store keeps the later arrival for an equal timestamp (#625), so this
 * follows it. Samples without a timestamp only replace nothing.
 */
export function isNewer(
  next: TelemetryLatestSample,
  current: TelemetryLatestSample | null,
): boolean {
  if (current === null) return true;
  if (next.t === null) return false;
  if (current.t === null) return true;
  const a = Date.parse(next.t);
  const b = Date.parse(current.t);
  if (Number.isNaN(a) || Number.isNaN(b))
    return next.t !== current.t || !sameValue(next.value, current.value);
  return a > b || (a === b && !sameValue(next.value, current.value));
}

export const MIN_BACKOFF_MS = 1_000;
export const MAX_BACKOFF_MS = 30_000;

/** Exponential reconnect delay: 1s, 2s, 4s … capped at 30s. `attempt` counts consecutive failures from 1. */
export function backoffMs(attempt: number): number {
  const n = Math.max(1, Math.floor(attempt));
  return Math.min(MAX_BACKOFF_MS, MIN_BACKOFF_MS * 2 ** (n - 1));
}

/**
 * gRPC codes after which reconnecting cannot help: not signed in (16), not allowed (7), a bad request (3),
 * or a server without the service (12). Anything else — a dropped connection, an API restart — is retried.
 */
export function isFatalCode(code: number | undefined): boolean {
  return code === 16 || code === 7 || code === 3 || code === 12;
}
