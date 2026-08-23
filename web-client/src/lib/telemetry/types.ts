/**
 * Domain types for telemetry, decoupled from the aspida `ValidTelemetryData`. UI reads these via
 * `lib/telemetry/repository.ts`, which routes everything through the unified `/telemetries/query`
 * endpoint (tier auto-selection) so the hot/warm/cold split stays an implementation detail.
 */

import type { ResolvedTelemetryValue } from "./value";

export type Granularity = "raw" | "hour" | "day";

/** One sample: ISO-8601 timestamp + numeric value. */
export type TelemetryPoint = { t: string; v: number };

export type TelemetrySeries = {
  pointId: string;
  points: TelemetryPoint[];
};

/**
 * One non-numeric (string/boolean) reading for the state timeline (#152 Phase B): ISO-8601 timestamp +
 * a display state string. Numeric readings never appear here — charts stay numeric-only.
 */
export type TelemetryStatePoint = { t: string; state: string };

export type TelemetryStateSeries = {
  pointId: string;
  points: TelemetryStatePoint[];
};

/**
 * A point's latest reading, of any value kind (#152). The wire's value fields are decoded once in
 * `mapping.ts` (via `value.ts`), so components never see `value`/`valueType`/`state`.
 */
export type TelemetryLatestSample = {
  /** ISO-8601 timestamp; null when the backend omitted it. */
  t: string | null;
  /** `{ kind: "none" }` when nothing was representable. */
  value: ResolvedTelemetryValue;
};

export type TelemetryQuery = {
  pointId: string;
  start?: Date;
  end?: Date;
  granularity?: Granularity;
  latest?: boolean;
};
